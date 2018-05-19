﻿using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Blazor;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorDB.Storage
{
    internal class StorageManagerLoad
    {
        public void LoadContextFromStorageOrCreateNew(IServiceCollection serviceCollection, Type contextType)
        {
            Logger.StartContextType(contextType);
            var storageSets = StorageManagerUtil.GetStorageSets(contextType);
            var stringModels = LoadStringModels(contextType, storageSets);
            //PrintStringModels(stringModels);
            stringModels = ScanNonAssociationModels(storageSets, stringModels);
            stringModels = ScanAssociationModels(storageSets, stringModels);
            stringModels = DeserializeModels(stringModels, storageSets);
            //PrintStringModels(stringModels);
            var context = CreateContext(contextType, stringModels);
            RegisterContext(serviceCollection, contextType, context);
            Logger.EndGroup();
        }

        private object CreateContext(Type contextType, Dictionary<Type, Dictionary<int, SerializedModel>> stringModels)
        {
            var context = Activator.CreateInstance(contextType);
            foreach (var prop in contextType.GetProperties())
            {
                if (prop.PropertyType.IsGenericType && prop.PropertyType.GetGenericTypeDefinition() == typeof(StorageSet<>))
                {
                    var modelType = prop.PropertyType.GetGenericArguments()[0];
                    var storageSetType = StorageManagerUtil.genericStorageSetType.MakeGenericType(modelType);
                    var storageTableName = Util.GetStorageTableName(contextType, modelType);
                    var metadata = StorageManagerUtil.LoadMetadata(storageTableName);
                    if (stringModels.ContainsKey(modelType))
                    {
                        var map = stringModels[modelType];
                        Logger.LoadModelInContext(modelType, map.Count);
                    }
                    else
                    {
                        Logger.LoadModelInContext(modelType, 0);
                    }
                    var storageSet = metadata != null ? LoadStorageSet(metadata, storageTableName, storageSetType, contextType, modelType, stringModels[modelType]) : CreateNewStorageSet(storageSetType);
                    prop.SetValue(context, storageSet);
                }
            }
            return context;
        }

        private Dictionary<Type, Dictionary<int, SerializedModel>> DeserializeModels(Dictionary<Type, Dictionary<int, SerializedModel>> stringModels, List<PropertyInfo> storageSets)
        {
            foreach (var map in stringModels)
            {
                Type modelType = map.Key;
                foreach (var sm in map.Value)
                {
                    var stringModel = sm.Value;
                    if (!stringModel.HasAssociation)
                    {
                        stringModel.Model = DeserializeModel(modelType, stringModel.StringModel);
                    }
                }
            }
            foreach (var map in stringModels) //TODO: Fix associations that are more than one level deep
            {
                Type modelType = map.Key;
                foreach (var sm in map.Value)
                {
                    var stringModel = sm.Value;
                    if (stringModel.Model == null)
                    {
                        var model = DeserializeModel(modelType, stringModel.StringModel);
                        foreach (var prop in model.GetType().GetProperties())
                        {
                            if (StorageManagerUtil.IsInContext(storageSets, prop) && prop.GetValue(model) != null)
                            {
                                var associatedLocalModel = prop.GetValue(model);
                                var localIdProp = associatedLocalModel.GetType().GetProperty(StorageManagerUtil.ID); //TODO: Handle missing Id prop
                                var localId = Convert.ToInt32(localIdProp.GetValue(associatedLocalModel));
                                var associatdRemoteModel = GetModelFromStringModels(stringModels, associatedLocalModel.GetType(), localId).Model;
                                prop.SetValue(model, associatdRemoteModel);
                            }
                        }
                        stringModel.Model = model;
                    }
                }
            }
            return stringModels;
        }

        private SerializedModel GetModelFromStringModels(Dictionary<Type, Dictionary<int, SerializedModel>> stringModels, Type type, int localId)
        {
            return stringModels[type][localId];
        }

        private object LoadStorageSet(Metadata metadata, string storageTableName, Type storageSetType, Type contextType, Type modelType, Dictionary<int, SerializedModel> map)
        {
            var instance = CreateNewStorageSet(storageSetType);
            var prop = storageSetType.GetProperty(StorageManagerUtil.STORAGE_CONTEXT_TYPE_NAME, StorageManagerUtil.flags);
            prop.SetValue(instance, Util.GetFullyQualifiedTypeName(contextType));
            var listGenericType = StorageManagerUtil.genericListType.MakeGenericType(modelType);
            var list = Activator.CreateInstance(listGenericType);
            foreach (var sm in map)
            {
                var stringModel = sm.Value;
                var addMethod = listGenericType.GetMethod(StorageManagerUtil.ADD);
                addMethod.Invoke(list, new object[] { stringModel.Model });
            }
            return SetList(instance, list);
        }

        private Dictionary<Type, Dictionary<int, SerializedModel>> ScanNonAssociationModels(List<PropertyInfo> storageSets, Dictionary<Type, Dictionary<int, SerializedModel>> stringModels)
        {
            foreach (var map in stringModels)
            {
                Type modelType = map.Key;
                foreach (var sm in map.Value)
                {
                    var stringModel = sm.Value;
                    if (!HasAssociation(storageSets, modelType, stringModel) && !HasListAssociation(storageSets, modelType, stringModel))
                    {
                        stringModel.HasAssociation = false;
                        stringModel.ScanDone = true;
                    }
                    else
                    {
                        stringModel.HasAssociation = true;
                    }
                }
            }
            return stringModels;
        }

        private bool HasAssociation(List<PropertyInfo> storageSets, Type modelType, SerializedModel stringModel)
        {
            var found = false;
            foreach (var prop in modelType.GetProperties())
            {
                if (StorageManagerUtil.IsInContext(storageSets, prop))
                {
                    found = true;
                    break;
                }
            }
            return found;
        }

        private bool HasListAssociation(List<PropertyInfo> storageSets, Type modelType, SerializedModel stringModel)
        {
            var found = false;
            foreach (var prop in modelType.GetProperties())
            {
                if (StorageManagerUtil.IsListInContext(storageSets, prop))
                {
                    found = true;
                    break;
                }
            }
            return found;
        }


        //TODO: The snippet below should also check to see that the model itself has no more associations to fix, not just if it has properties.
        /*
         * if(HasAssociation(storageSets, modelType, stringModel))
            {
                stringModel.StringModel = FixAssociationsInStringModels(stringModel, modelType, storageSets, stringModels);
                stringModel.ScanDone = true;
            }*/
        private Dictionary<Type, Dictionary<int, SerializedModel>> ScanAssociationModels(List<PropertyInfo> storageSets, Dictionary<Type, Dictionary<int, SerializedModel>> stringModels)
        {
            var count = 0;
            do
            {
                count++;
                foreach (var map in stringModels)
                {
                    Type modelType = map.Key;
                    foreach (var sm in map.Value)
                    {
                        var stringModel = sm.Value;
                        if (!stringModel.ScanDone)
                        {
                            if (HasAssociation(storageSets, modelType, stringModel) || HasListAssociation(storageSets, modelType, stringModel))
                            {
                                stringModel.StringModel = FixAssociationsInStringModels(stringModel, modelType, storageSets, stringModels);
                                stringModel.ScanDone = true;
                            }
                            else
                            {
                                stringModel.ScanDone = true;
                            }
                        }
                    }
                }
                if (count == 20) break; //Go 20 deep throw exception here?
            } while (IsScanDone(stringModels));
            return stringModels;
        }

        void PrintStringModels(Dictionary<Type, Dictionary<int, SerializedModel>> stringModels)
        {
            foreach (var map in stringModels)
            {
                Type modelType = map.Key;
                Console.WriteLine("-----------");
                Console.WriteLine("modelType: {0}", modelType.Name);
                foreach (var sm in map.Value)
                {
                    var stringModel = sm.Value;
                    Console.WriteLine("Key: {0}", sm.Key);
                    Console.WriteLine("sm: {0}", sm.Value.StringModel);
                    Console.WriteLine("Is Done: {0}", sm.Value.ScanDone);
                    Console.WriteLine("Has Model: {0}", sm.Value.Model != null);
                }
            }

        }

        private string FixAssociationsInStringModels(SerializedModel stringModel, Type modelType, List<PropertyInfo> storageSets, Dictionary<Type, Dictionary<int, SerializedModel>> stringModels)
        {
            var result = stringModel.StringModel;
            foreach (var prop in modelType.GetProperties())
            {
                if (StorageManagerUtil.IsInContext(storageSets, prop))
                {
                    if(TryGetIdFromSerializedModel(result, prop.Name, out var id))
                    {
                        var updated = GetAssociatedStringModel(stringModels, prop.PropertyType, id);
                        result = ReplaceIdWithAssociation(result, prop.Name, id, updated);
                    }
                }
                if (StorageManagerUtil.IsListInContext(storageSets, prop))
                {
                    if(TryGetIdListFromSerializedModel(result, prop.Name, out var idList))
                    {
                        var sb = new StringBuilder();
                        foreach (var id in idList)
                        {
                            var updated = GetAssociatedStringModel(stringModels, prop.PropertyType.GetGenericArguments()[0], id);
                            sb.Append(updated).Append(",");
                        }
                        var strList = sb.ToString().Substring(0, sb.ToString().Length - 1);
                        result = ReplaceListWithAssociationList(result, prop.Name, strList);
                    }
                }
            }
            return result;
        }

        private string ReplaceListWithAssociationList(string serializedModel, string propName, string strList)
        {
            var propStart = serializedModel.IndexOf($"\"{propName}\":[");
            var start = serializedModel.IndexOf('[', propStart) + 1;
            var end = serializedModel.IndexOf(']', start);
            var result = StorageManagerUtil.ReplaceString(serializedModel, start, end, strList);
            return result;
        }

        private bool TryGetIdListFromSerializedModel(string serializedModel, string propName, out List<int> idList)
        {
            var list = new List<int>();
            if(serializedModel.IndexOf($"\"{propName}\":null") != -1)
            {
                idList = list;
                return false;
            }
            var propStart = serializedModel.IndexOf($"\"{propName}\":[");
            var start = serializedModel.IndexOf('[', propStart) + 1;
            var end = serializedModel.IndexOf(']', start);
            var stringlist = serializedModel.Substring(start, end - start);
            var arr = stringlist.Split(',');
            foreach(var s in arr)
            {
                list.Add(Convert.ToInt32(s));
            }
            idList = list;
            return true;
        }

        private string GetAssociatedStringModel(Dictionary<Type, Dictionary<int, SerializedModel>> stringModels, Type modelType, int id)
        {
            var map = stringModels[modelType];
            return map[id].StringModel;
        }

        //TODO: Convert to Linq
        private bool IsScanDone(Dictionary<Type, Dictionary<int, SerializedModel>> stringModels)
        {
            var done = true;
            foreach (var map in stringModels.Values)
            {
                foreach (var sm in map.Values)
                {
                    if (!sm.ScanDone) done = false;
                }
            }
            return done;
        }

        private Dictionary<Type, Dictionary<int, SerializedModel>> LoadStringModels(Type contextType, List<PropertyInfo> storageSets)
        {
            var stringModels = new Dictionary<Type, Dictionary<int, SerializedModel>>();
            foreach (var prop in storageSets)
            {
                var modelType = prop.PropertyType.GetGenericArguments()[0];
                var map = new Dictionary<int, SerializedModel>();
                var storageTableName = Util.GetStorageTableName(contextType, modelType);
                var metadata = StorageManagerUtil.LoadMetadata(storageTableName);
                if (metadata != null)
                {
                    foreach (var guid in metadata.Guids)
                    {
                        var name = $"{storageTableName}-{guid}";
                        var serializedModel = BlazorDBInterop.GetItem(name, false);
                        var Id = FindIdInSerializedModel(serializedModel);
                        map.Add(Id, new SerializedModel { StringModel = serializedModel });
                    }
                    stringModels.Add(modelType, map);
                }
            }
            return stringModels;
        }

        //TODO: Verify that the found id is at the top level in case of nested objects
        private bool TryGetIdFromSerializedModel(string serializedModel, string propName, out int id)
        {
            if (serializedModel.IndexOf($"\"{propName}\":null") != -1)
            {
                id = -1;
                return false;
            }
            var propStart = serializedModel.IndexOf($"\"{propName}\":");
            var start = serializedModel.IndexOf(':', propStart);
            id = GetIdFromString(serializedModel, start);
            return true;
        }

        //TODO: Verify that the found id is at the top level in case of nested objects
        private int FindIdInSerializedModel(string serializedModel)
        {
            var start = serializedModel.IndexOf($"\"{StorageManagerUtil.ID}\":");
            return GetIdFromString(serializedModel, start);
        }

        private int GetIdFromString(string stringToSearch, int startFrom = 0)
        {
            var foundFirst = false;
            var arr = stringToSearch.ToCharArray();
            var result = new List<char>();
            for (int i = startFrom; i < arr.Length; i++)
            {
                var ch = arr[i];
                if (Char.IsDigit(ch))
                {
                    foundFirst = true;
                    result.Add(ch);
                }
                else
                {
                    if (foundFirst) break;
                }
            }
            return Convert.ToInt32(new string(result.ToArray()));
        }

        private string ReplaceIdWithAssociation(string result, string name, int id, string stringModel)
        {
            var stringToFind = $"\"{name}\":{id}";
            var nameIndex = result.IndexOf(stringToFind);
            var index = result.IndexOf(id.ToString(), nameIndex);
            result = StorageManagerUtil.ReplaceString(result, index, index + id.ToString().Length, stringModel);
            return result;
        }
        
        private int GetListCount(StorageContext context, PropertyInfo prop)
        {
            var list = prop.GetValue(context);
            var countProp = list.GetType().GetProperty(StorageManagerUtil.COUNT);
            return (int)countProp.GetValue(list);
        }

        private object CreateNewStorageSet(Type storageSetType)
        {
            return Activator.CreateInstance(storageSetType);
        }
        
        private object SetList(object instance, object list)
        {
            var prop = instance.GetType().GetProperty(StorageManagerUtil.LIST, StorageManagerUtil.flags);
            prop.SetValue(instance, list);
            return instance;
        }

        private object DeserializeModel(Type modelType, string value)
        {
            var method = typeof(JsonWrapper).GetMethod(StorageManagerUtil.DESERIALIZE);
            var genericMethod = method.MakeGenericMethod(modelType);
            var model = genericMethod.Invoke(new JsonWrapper(), new object[] { value });
            return model;
        }

        private void RegisterContext(IServiceCollection serviceCollection, Type type, object context)
        {
            serviceCollection.AddSingleton(
                serviceType: type,
                implementationInstance: context);
        }
    }

    class JsonWrapper
    {
        public T Deserialize<T>(string value) => JsonUtil.Deserialize<T>(value);
    }
}