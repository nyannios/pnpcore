﻿using PnP.Core.Services;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace PnP.Core.Model.SharePoint
{
    internal static class ChangeCollectionHandler
    {
        internal static ApiCall GetApiCall<TModel>(IDataModel<TModel> parent, ChangeQueryOptions query)
        {
            return new ApiCall(GetApiCallUrl(parent), ApiType.SPORest, GetChangeQueryBody(query));
        }

        internal static string GetApiCallUrl<TModel>(IDataModel<TModel> parent)
        {
            var type = typeof(IChange);
            // Translate any provided interface type into the corresponding concrete type
            var concreteType = type.GetCustomAttribute<ConcreteTypeAttribute>();
            if (concreteType == null)
            {
                throw new ClientException(ErrorType.ModelMetadataIncorrect, string.Format(PnPCoreResources.Exception_ModelMetadataIncorrect_MissingConcreteTypeAttribute, type.Name));
            }

            return EntityManager.GetClassInfo<object>(concreteType.Type, null, parent)?.SharePointGet;
        }

        internal static string GetChangeQueryBody(ChangeQueryOptions query)
        {
            dynamic innerQuery = new
            {
                __metadata = new { type = "SP.ChangeQuery" },
                query.Activity,
                query.Add,
                query.Alert,
                query.ContentType,
                query.DeleteObject,
                // If we use a non-string, SharePoint yells at us about being unable to convert a primitive to Edm.Int64
                FetchLimit = query.FetchLimit.ToString(),
                query.Field,
                query.File,
                query.Folder,
                query.Group,
                query.GroupMembershipAdd,
                query.GroupMembershipDelete,
                query.IgnoreStartTokenNotFoundError,
                query.Item,
                query.LatestFirst,
                query.List,
                query.Move,
                query.Navigation,
                query.RecursiveAll,
                query.Rename,
                query.RequireSecurityTrim,
                query.Restore,
                query.RoleAssignmentAdd,
                query.RoleAssignmentDelete,
                query.RoleDefinitionAdd,
                query.RoleDefinitionDelete,
                query.RoleDefinitionUpdate,
                query.SecurityPolicy,
                query.Site,
                query.SystemUpdate,
                query.Update,
                query.User,
                query.View,
                query.Web
            }.AsExpando();

            if (query.ChangeTokenStart != null)
            {
                innerQuery.ChangeTokenStart = new
                {
                    __metadata = new { type = "SP.ChangeToken" },
                    query.ChangeTokenStart.StringValue
                };
            }

            if (query.ChangeTokenEnd != null)
            {
                innerQuery.ChangeTokenEnd = new
                {
                    __metadata = new { type = "SP.ChangeToken" },
                    query.ChangeTokenEnd.StringValue
                };
            }

            dynamic bodyQuery = new ExpandoObject();
            bodyQuery.query = innerQuery;

            return JsonSerializer.Serialize(bodyQuery as ExpandoObject, new JsonSerializerOptions() { IgnoreNullValues = true });
        }

        internal static IEnumerable<IChange> Deserialize(ApiCallResponse response)
        {
            if (string.IsNullOrEmpty(response.Json))
            {
                throw new ArgumentNullException(nameof(response.Json));
            }

            var result = new List<IChange>();
            var document = JsonSerializer.Deserialize<JsonElement>(response.Json);

            if (document.TryGetProperty("d", out JsonElement dRoot) && dRoot.TryGetProperty("results", out JsonElement dataRows))
            {
                // No data returned, stop processing
                if (dataRows.GetArrayLength() == 0)
                {
                    return result;
                }

                foreach (var row in dataRows.EnumerateArray())
                {
                    var pnpObject = GetConcreteInstance(row);
                    if (pnpObject != null)
                    {
                        ProcessChangeElement(pnpObject, row, response.BatchRequestId);
                        result.Add(pnpObject as IChange);
                    }
                }
            }

            return result;
        }

        private static void ProcessChangeElement(object pnpObject, JsonElement element, Guid batchRequestId)
        {
            var metadataBasedObject = pnpObject as IMetadataExtensible;
            var entity = EntityManager.GetClassInfo<IChange>(pnpObject.GetType(), null);

            SetBatchRequestId(pnpObject as TransientObject, batchRequestId);

            // Enumerate the received properties and try to map them to the model
            foreach (var property in element.EnumerateObject())
            {
                // Find the model field linked to this field
                EntityFieldInfo entityField = entity.Fields.FirstOrDefault(p => p.SharePointName == property.Name);

                // Entity field should be populated for the actual fields we've requested
                if (entityField != null)
                {
                    if (entityField.PropertyInfo.PropertyType == typeof(IChangeToken)) // Special case
                    {
                        var changeToken = GetConcreteInstance(property.Value);
                        ProcessChangeElement(changeToken, property.Value, batchRequestId);
                        entityField.PropertyInfo?.SetValue(pnpObject, changeToken);
                    }
                    else if (entityField.PropertyInfo.PropertyType == typeof(IContentType))
                    {
                        // Since this is an SP.ContentTypeId and NOT an SP.ContentType, it's not a perfect entity match; we'll do it manually
                        var concreteInstance = (IContentType)EntityManager.GetEntityConcreteInstance(entityField.PropertyInfo.PropertyType);
                        var contentTypeId = property.Value.GetProperty("StringValue").GetString();
                        concreteInstance.SetSystemProperty(ct => ct.Id, contentTypeId);
                        concreteInstance.SetSystemProperty(ct => ct.StringId, contentTypeId);
                        (concreteInstance as IMetadataExtensible).Metadata.Add(PnPConstants.MetaDataType, "SP.ContentTypeId");
                        
                        entityField.PropertyInfo?.SetValue(pnpObject, concreteInstance);
                    }
                    else if (entityField.PropertyInfo.PropertyType.ImplementsInterface(typeof(IDataModel<>)))
                    {
                        var concreteInstance = EntityManager.GetEntityConcreteInstance(entityField.PropertyInfo.PropertyType);
                        ProcessChangeElement(concreteInstance, property.Value, batchRequestId);
                        entityField.PropertyInfo?.SetValue(pnpObject, concreteInstance);
                    }
                    else // Simple property mapping
                    {
                        if (!string.IsNullOrEmpty(entityField.SharePointJsonPath))
                        {
                            var jsonPathFields = entity.Fields.Where(p => !string.IsNullOrEmpty(p.SharePointName) && p.SharePointName.Equals(entityField.SharePointName)).ToList();
                            if (jsonPathFields.Any())
                            {
                                foreach (var jsonPathField in jsonPathFields)
                                {
                                    var jsonElement = JsonMappingHelper.GetJsonElementFromPath(property.Value, jsonPathField.SharePointJsonPath);

                                    // Don't assume that the requested json path was also loaded. When using the QueryProperties model there can be 
                                    // a json object returned that does have all properties loaded 
                                    if (!jsonElement.Equals(property.Value))
                                    {
                                        jsonPathField.PropertyInfo?.SetValue(pnpObject, JsonMappingHelper.GetJsonFieldValue(null, jsonPathField.Name,
                                            jsonElement, jsonPathField.DataType, jsonPathField.SharePointUseCustomMapping, null));
                                    }
                                }
                            }
                        }
                        else
                        {
                            // Set the object property value taken from the JSON payload
                            entityField.PropertyInfo?.SetValue(pnpObject, JsonMappingHelper.GetJsonFieldValue(null, entityField.Name,
                                property.Value, entityField.DataType, entityField.SharePointUseCustomMapping, null));
                        }
                    }
                }
                else
                {
                    // Let's keep track of the object metadata, useful when creating new requests
                    if (property.Name == "__metadata")
                    {
                        JsonMappingHelper.TrackSharePointMetaData(metadataBasedObject, property);
                    }
                    else if (property.Name == "__deferred")
                    {
                        // Let's keep track of these "pointers". Not sure we can do anything with them, but I don't want to lose them just yet.

                        // __deferred property
                        //"__deferred": {
                        //    "uri": "https://bertonline.sharepoint.com/sites/modern/_api/site/RootWeb/WorkflowAssociations"
                        //}

                        if (!metadataBasedObject.Metadata.ContainsKey("deferredUri"))
                        {
                            metadataBasedObject.Metadata.Add("deferredUri", property.Value.GetProperty("uri").GetString());
                        }
                    }
                }
            }
        }

        private static object GetConcreteInstance(JsonElement row)
        {
            if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty(PnPConstants.SharePointRestMetadata, out JsonElement metadata) && metadata.TryGetProperty(PnPConstants.MetaDataType, out JsonElement type))
            {
                switch (type.GetString())
                {
                    case "SP.Change":
                        return new Change();
                    case "SP.ChangeAlert":
                        return new ChangeAlert();
                    case "SP.ChangeContentType":
                        return new ChangeContentType();
                    case "SP.ChangeField":
                        return new ChangeField();
                    case "SP.ChangeFile":
                        return new ChangeFile();
                    case "SP.ChangeFolder":
                        return new ChangeFolder();
                    case "SP.ChangeGroup":
                        return new ChangeGroup();
                    case "SP.ChangeItem":
                        return new ChangeItem();
                    case "SP.ChangeList":
                        return new ChangeList();
                    case "SP.ChangeSite":
                        return new ChangeSite();
                    case "SP.ChangeToken":
                        return new ChangeToken();
                    case "SP.ChangeUser":
                        return new ChangeUser();
                    case "SP.ChangeView":
                        return new ChangeView();
                    case "SP.ChangeWeb":
                        return new ChangeWeb();
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            return null;
        }

        private static void SetBatchRequestId(TransientObject pnpObject, Guid batchRequestId)
        {
            if (pnpObject != null)
            {
                pnpObject.BatchRequestId = batchRequestId;
            }
        }
    }
}