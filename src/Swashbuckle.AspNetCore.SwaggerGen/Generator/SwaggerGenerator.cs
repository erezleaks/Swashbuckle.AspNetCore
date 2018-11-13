using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Options;
using Swashbuckle.AspNetCore.Swagger;
using Microsoft.OpenApi.Models;
using Microsoft.AspNetCore.Mvc;

namespace Swashbuckle.AspNetCore.SwaggerGen
{
    public class SwaggerGenerator : ISwaggerProvider
    {
        private readonly IApiDescriptionGroupCollectionProvider _apiDescriptionsProvider;
        private readonly ISchemaRegistryFactory _schemaRegistryFactory;
        private readonly SwaggerGeneratorOptions _options;

        public SwaggerGenerator(
            IApiDescriptionGroupCollectionProvider apiDescriptionsProvider,
            ISchemaRegistryFactory schemaRegistryFactory,
            IOptions<SwaggerGeneratorOptions> optionsAccessor)
            : this (apiDescriptionsProvider, schemaRegistryFactory, optionsAccessor.Value)
        { }

        public SwaggerGenerator(
            IApiDescriptionGroupCollectionProvider apiDescriptionsProvider,
            ISchemaRegistryFactory schemaRegistryFactory,
            SwaggerGeneratorOptions options)
        {
            _apiDescriptionsProvider = apiDescriptionsProvider;
            _schemaRegistryFactory = schemaRegistryFactory;
            _options = options ?? new SwaggerGeneratorOptions();
        }

        public OpenApiDocument GetSwagger(string documentName, string host = null, string basePath = null)
        {
            if (!_options.SwaggerDocs.TryGetValue(documentName, out OpenApiInfo info))
                throw new UnknownSwaggerDocument(documentName);

            var applicableApiDescriptions = _apiDescriptionsProvider.ApiDescriptionGroups.Items
                .SelectMany(group => group.Items)
                .Where(apiDesc => _options.DocInclusionPredicate(documentName, apiDesc))
                .Where(apiDesc => !_options.IgnoreObsoleteActions || !apiDesc.IsObsolete());

            var schemaRegistry = _schemaRegistryFactory.Create();

            var swaggerDoc = new OpenApiDocument
            {
                Info = info,
                Servers = CreateServers(host, basePath),
                Paths = CreatePaths(applicableApiDescriptions, schemaRegistry),
                Components = new OpenApiComponents
                {
                    Schemas = schemaRegistry.Schemas,
                    SecuritySchemes = _options.SecuritySchemes
                }
            };

            var filterContext = new DocumentFilterContext(
                _apiDescriptionsProvider.ApiDescriptionGroups,
                applicableApiDescriptions,
                schemaRegistry);

            foreach (var filter in _options.DocumentFilters)
            {
                filter.Apply(swaggerDoc, filterContext);
            }

            return swaggerDoc;
        }

        private IList<OpenApiServer> CreateServers(string host, string basePath)
        {
            return (host == null && basePath == null)
                ? new List<OpenApiServer>()
                : new List<OpenApiServer> { new OpenApiServer { Url = $"{host}/{basePath}" } };
        }

        private OpenApiPaths CreatePaths(IEnumerable<ApiDescription> apiDescriptions, ISchemaRegistry schemaRegistry)
        {
            var apiDescriptionsByPath = apiDescriptions
                .OrderBy(_options.SortKeySelector)
                .GroupBy(apiDesc => apiDesc.RelativePathSansQueryString());

            var paths = new OpenApiPaths();
            foreach (var group in apiDescriptionsByPath)
            {
                paths.Add($"/{group.Key}", CreatePathItem(group, schemaRegistry));
            };

            return paths;
        }

        private OpenApiPathItem CreatePathItem(IEnumerable<ApiDescription> apiDescriptions, ISchemaRegistry schemaRegistry)
        {
            return new OpenApiPathItem
            {
                Operations = CreateOperations(apiDescriptions, schemaRegistry)
            };
        }

        private IDictionary<OperationType, OpenApiOperation> CreateOperations(IEnumerable<ApiDescription> apiDescriptions, ISchemaRegistry schemaRegistry)
        {
            var apiDescriptionsByMethod = apiDescriptions
                .OrderBy(_options.SortKeySelector)
                .GroupBy(apiDesc => apiDesc.HttpMethod);

            var operations = new Dictionary<OperationType, OpenApiOperation>();

            foreach (var group in apiDescriptionsByMethod)
            {
                var httpMethod = group.Key;

                if (httpMethod == null)
                    throw new NotSupportedException(string.Format(
                        "Ambiguous HTTP method for action - {0}. " +
                        "Actions require an explicit HttpMethod binding for Swagger 2.0",
                        group.First().ActionDescriptor.DisplayName));

                if (group.Count() > 1 && _options.ConflictingActionsResolver == null)
                    throw new NotSupportedException(string.Format(
                        "HTTP method \"{0}\" & path \"{1}\" overloaded by actions - {2}. " +
                        "Actions require unique method/path combination for Swagger 2.0. Use ConflictingActionsResolver as a workaround",
                        httpMethod,
                        group.First().RelativePathSansQueryString(),
                        string.Join(",", group.Select(apiDesc => apiDesc.ActionDescriptor.DisplayName))));

                var apiDescription = (group.Count() > 1) ? _options.ConflictingActionsResolver(group) : group.Single();

                operations.Add(OperationTypeMap[httpMethod.ToUpper()], CreateOperation(apiDescription, schemaRegistry));
            };

            return operations;
        }

        private OpenApiOperation CreateOperation(ApiDescription apiDescription, ISchemaRegistry schemaRegistry)
        {
            apiDescription.GetAdditionalMetadata(out MethodInfo methodInfo, out IEnumerable<object> methodAttributes);

            var operation = new OpenApiOperation
            {
                Tags = CreateTags(apiDescription),
                OperationId = _options.OperationIdSelector(apiDescription),
                Parameters = CreateParameters(apiDescription, schemaRegistry),
                RequestBody = CreateRequestBody(apiDescription, methodAttributes, schemaRegistry),
                Deprecated = methodAttributes.OfType<ObsoleteAttribute>().Any()
            };

            var filterContext = new OperationFilterContext(
                apiDescription,
                schemaRegistry,
                methodInfo);

            foreach (var filter in _options.OperationFilters)
            {
                filter.Apply(operation, filterContext);
            }

            return operation;
        }

        private IList<OpenApiTag> CreateTags(ApiDescription apiDescription)
        {
            return _options.TagsSelector(apiDescription)
                .Select(tagName => new OpenApiTag { Name = tagName })
                .ToList();
        }

        private IList<OpenApiParameter> CreateParameters(ApiDescription apiDescription, ISchemaRegistry schemaRegistry)
        {
            var applicableApiParameterDescriptions = apiDescription.ParameterDescriptions
                .Where(apiParam =>
                {
                    return ParameterLocationMap.Keys.Contains(apiParam.Source)
                        && (apiParam.ModelMetadata == null || apiParam.ModelMetadata.IsBindingAllowed);
                });

            return applicableApiParameterDescriptions
                .Select(apiParam => CreateParameter(apiDescription, apiParam, schemaRegistry))
                .ToList();
        }

        private OpenApiParameter CreateParameter(
            ApiDescription apiDescription,
            ApiParameterDescription apiParameterDescription,
            ISchemaRegistry schemaRegistry)
        {
            apiParameterDescription.GetAdditionalMetadata(
                apiDescription,
                out ParameterInfo parameterInfo,
                out PropertyInfo propertyInfo,
                out IEnumerable<object> parameterOrPropertyAttributes);

            var name = _options.DescribeAllParametersInCamelCase
                ? apiParameterDescription.Name.ToCamelCase()
                : apiParameterDescription.Name;

            var isRequired = (apiParameterDescription.Source == BindingSource.Path)
                || parameterOrPropertyAttributes.Any(attr => RequiredAttributeTypes.Contains(attr.GetType()));

            var schema = (apiParameterDescription.Type != null)
                ? schemaRegistry.GetOrRegister(apiParameterDescription.Type)
                : new OpenApiSchema { Type = "string" };

            var parameter = new OpenApiParameter
            {
                Name = name,
                In = ParameterLocationMap[apiParameterDescription.Source],
                Required = isRequired,
                Schema = schema
            };

            var filterContext = new ParameterFilterContext(
                apiParameterDescription,
                schemaRegistry,
                parameterInfo,
                propertyInfo);

            foreach (var filter in _options.ParameterFilters)
            {
                filter.Apply(parameter, filterContext);
            }

            return parameter;
        }

        private OpenApiRequestBody CreateRequestBody(
            ApiDescription apiDescription,
            IEnumerable<object> methodAttributes,
            ISchemaRegistry schemaRegistry)
        {
            var supportedContentTypes = InferSupportedContentTypes(apiDescription, methodAttributes);
            if (!supportedContentTypes.Any()) return null;

            return new OpenApiRequestBody
            {
                Content = supportedContentTypes
                    .ToDictionary(
                        contentType => contentType,
                        contentType => CreateRequestMediaType(contentType, apiDescription, schemaRegistry)
                    )
            };
        }

        private IEnumerable<string> InferSupportedContentTypes(ApiDescription apiDescription, IEnumerable<object> methodAttributes)
        {
            // If there's content types explicitly specified via ConsumesAttribute, use them
            var explicitContentTypes = methodAttributes.OfType<ConsumesAttribute>()
                .SelectMany(attr => attr.ContentTypes)
                .Distinct();
            if (explicitContentTypes.Any()) return explicitContentTypes;

            // If there's content types surfaced by ApiExplorer, use them
            var apiExplorerContentTypes = apiDescription.SupportedRequestFormats
                .Select(format => format.MediaType);
            if (apiExplorerContentTypes.Any()) return apiExplorerContentTypes;

            // As a last resort, try to infer from parameter bindings
            return apiDescription.ParameterDescriptions.Any(apiParam => FormBindingSources.Contains(apiParam.Source))
                ? new[] { "multipart/form-data" }
                : Enumerable.Empty<string>();
        }

        private OpenApiMediaType CreateRequestMediaType(string contentType, ApiDescription apiDescription, ISchemaRegistry schemaRegistry)
        {
            var bodyParameter = apiDescription.ParameterDescriptions
                .FirstOrDefault(apiParam => apiParam.Source == BindingSource.Body);

            var formParameters = apiDescription.ParameterDescriptions
                .Where(apiParam => FormBindingSources.Contains(apiParam.Source));

            if (bodyParameter == null && !formParameters.Any())
                throw new InvalidOperationException("TODO:");

            return new OpenApiMediaType
            {
                Schema = (bodyParameter != null)
                    ? schemaRegistry.GetOrRegister(bodyParameter.Type)
                    : CreateFormSchema(apiDescription, formParameters, schemaRegistry)
            };
        }

        private OpenApiSchema CreateFormSchema(
            ApiDescription apiDescription,
            IEnumerable<ApiParameterDescription> formParameters,
            ISchemaRegistry schemaRegistry)
        {
            // First, map to a simple data structure that captures the pertinent values
            var parametersMetadata = formParameters
                .Select(apiParam =>
                {
                    apiParam.GetAdditionalMetadata(
                        apiDescription,
                        out ParameterInfo parameterInfo,
                        out PropertyInfo propertyInfo,
                        out IEnumerable<object> parameterOrPropertyAttributes);

                    var name = _options.DescribeAllParametersInCamelCase ? apiParam.Name.ToCamelCase() : apiParam.Name;

                    var isRequired = parameterOrPropertyAttributes.Any(attr => RequiredAttributeTypes.Contains(attr.GetType()));

                    var schema = schemaRegistry.GetOrRegister(apiParam.Type);

                    return new
                    {
                        Name = name,
                        IsRequired = isRequired,
                        Schema = schema
                    };
                });

            return new OpenApiSchema
            {
                Type = "object",
                Properties = parametersMetadata.ToDictionary(
                    metadata => metadata.Name,
                    metadata => metadata.Schema
                ),
                Required = new SortedSet<string>(parametersMetadata.Where(m => m.IsRequired).Select(m => m.Name))
            };
        }

        //public SwaggerDocument GetSwagger(
        //    string documentName,
        //    string host = null,
        //    string basePath = null,
        //    string[] schemes  null)
        //{
        //    if (!_options.SwaggerDocs.TryGetValue(documentName, out Info info))
        //        throw new UnknownSwaggerDocument(documentName);

        //    var applicableApiDescriptions = _apiDescriptionsProvider.ApiDescriptionGroups.Items
        //        .SelectMany(group => group.Items)
        //        .Where(apiDesc => _options.DocInclusionPredicate(documentName, apiDesc))
        //        .Where(apiDesc => !_options.IgnoreObsoleteActions || !apiDesc.IsObsolete());

        //    var schemaRegistry = _schemaRegistryFactory.Create();

        //    var swaggerDoc = new SwaggerDocument
        //    {
        //        Info = info,
        //        Host = host,
        //        BasePath = basePath,
        //        Schemes = schemes,
        //        Paths = CreatePathItems(applicableApiDescriptions, schemaRegistry),
        //        Definitions = schemaRegistry.Definitions,
        //        SecurityDefinitions = _options.SecurityDefinitions.Any() ? _options.SecurityDefinitions : null,
        //        Security = _options.SecurityRequirements.Any() ? _options.SecurityRequirements : null
        //    };

        //    var filterContext = new DocumentFilterContext(
        //        _apiDescriptionsProvider.ApiDescriptionGroups,
        //        applicableApiDescriptions,
        //        schemaRegistry);

        //    foreach (var filter in _options.DocumentFilters)
        //    {
        //        filter.Apply(swaggerDoc, filterContext);
        //    }

        //    return swaggerDoc;
        //}

        //private Dictionary<string, PathItem> CreatePathItems(
        //    IEnumerable<ApiDescription> apiDescriptions,
        //    ISchemaRegistry schemaRegistry)
        //{
        //    return apiDescriptions
        //        .OrderBy(_options.SortKeySelector)
        //        .GroupBy(apiDesc => apiDesc.RelativePathSansQueryString())
        //        .ToDictionary(group => "/" + group.Key, group => CreatePathItem(group, schemaRegistry));
        //}

        //private Operation CreateOperation(
        //    ApiDescription apiDescription,
        //    ISchemaRegistry schemaRegistry)
        //{
        //    // Try to retrieve additional metadata that's not provided by ApiExplorer
        //    MethodInfo methodInfo;
        //    var customAttributes = Enumerable.Empty<object>();

        //    if (apiDescription.TryGetMethodInfo(out methodInfo))
        //    {
        //        customAttributes = methodInfo.GetCustomAttributes(true)
        //            .Union(methodInfo.DeclaringType.GetTypeInfo().GetCustomAttributes(true));
        //    }

        //    var isDeprecated = customAttributes.Any(attr => attr.GetType() == typeof(ObsoleteAttribute));

        //    var operation = new Operation
        //    {
        //        OperationId = _options.OperationIdSelector(apiDescription),
        //        Tags = _options.TagsSelector(apiDescription),
        //        Consumes = CreateConsumes(apiDescription, customAttributes),
        //        Produces = CreateProduces(apiDescription, customAttributes),
        //        Parameters = CreateParameters(apiDescription, schemaRegistry),
        //        Responses = CreateResponses(apiDescription, schemaRegistry),
        //        Deprecated = isDeprecated ? true : (bool?)null
        //    };

        //    // Assign default value for Consumes if not yet assigned AND operation contains form params
        //    if (operation.Consumes.Count() == 0 && operation.Parameters.Any(p => p.In == "formData"))
        //    {
        //        operation.Consumes.Add("multipart/form-data");
        //    }

        //    var filterContext = new OperationFilterContext(
        //        apiDescription,
        //        schemaRegistry,
        //        methodInfo);

        //    foreach (var filter in _options.OperationFilters)
        //    {
        //        filter.Apply(operation, filterContext);
        //    }

        //    return operation;
        //}

        //private IList<string> CreateConsumes(ApiDescription apiDescription, IEnumerable<object> customAttributes)
        //{
        //    var consumesAttribute = customAttributes.OfType<ConsumesAttribute>().FirstOrDefault();

        //    var mediaTypes = (consumesAttribute != null)
        //        ? consumesAttribute.ContentTypes
        //        : apiDescription.SupportedRequestFormats
        //            .Select(apiRequestFormat => apiRequestFormat.MediaType);

        //    return mediaTypes.ToList();
        //}

        //private IList<string> CreateProduces(ApiDescription apiDescription, IEnumerable<object> customAttributes)
        //{
        //    var producesAttribute = customAttributes.OfType<ProducesAttribute>().FirstOrDefault();

        //    var mediaTypes = (producesAttribute != null)
        //        ? producesAttribute.ContentTypes
        //        : apiDescription.SupportedResponseTypes
        //            .SelectMany(apiResponseType => apiResponseType.ApiResponseFormats)
        //            .Select(apiResponseFormat => apiResponseFormat.MediaType)
        //            .Distinct();

        //    return mediaTypes.ToList();
        //}

        //private IList<IParameter> CreateParameters(
        //    ApiDescription apiDescription,
        //    ISchemaRegistry schemaRegistry)
        //{
        //    var applicableParamDescriptions = apiDescription.ParameterDescriptions
        //        .Where(paramDesc =>
        //        {
        //            return paramDesc.Source.IsFromRequest
        //                && (paramDesc.ModelMetadata == null || paramDesc.ModelMetadata.IsBindingAllowed);
        //        });

        //    return applicableParamDescriptions
        //        .Select(paramDesc => CreateParameter(apiDescription, paramDesc, schemaRegistry))
        //        .ToList();
        //}

        //private IParameter CreateParameter(
        //    ApiDescription apiDescription,
        //    ApiParameterDescription apiParameterDescription,
        //    ISchemaRegistry schemaRegistry)
        //{
        //    // Try to retrieve additional metadata that's not directly provided by ApiExplorer
        //    ParameterInfo parameterInfo = null;
        //    PropertyInfo propertyInfo = null;
        //    var customAttributes = Enumerable.Empty<object>();

        //    if (apiParameterDescription.TryGetParameterInfo(apiDescription, out parameterInfo))
        //        customAttributes = parameterInfo.GetCustomAttributes(true);
        //    else if (apiParameterDescription.TryGetPropertyInfo(out propertyInfo))
        //        customAttributes = propertyInfo.GetCustomAttributes(true);

        //    var name = _options.DescribeAllParametersInCamelCase
        //        ? apiParameterDescription.Name.ToCamelCase()
        //        : apiParameterDescription.Name;

        //    var isRequired = customAttributes.Any(attr =>
        //        new[] { typeof(RequiredAttribute), typeof(BindRequiredAttribute) }.Contains(attr.GetType()));

        //    var parameter = (apiParameterDescription.Source == BindingSource.Body)
        //        ? CreateBodyParameter(
        //            apiParameterDescription,
        //            name,
        //            isRequired,
        //            schemaRegistry)
        //        : CreateNonBodyParameter(
        //            apiParameterDescription,
        //            parameterInfo,
        //            customAttributes,
        //            name,
        //            isRequired,
        //            schemaRegistry);

        //    var filterContext = new ParameterFilterContext(
        //        apiParameterDescription,
        //        schemaRegistry,
        //        parameterInfo,
        //        propertyInfo);

        //    foreach (var filter in _options.ParameterFilters)
        //    {
        //        filter.Apply(parameter, filterContext);
        //    }

        //    return parameter;
        //}

        //private IParameter CreateBodyParameter(
        //    ApiParameterDescription apiParameterDescription,
        //    string name,
        //    bool isRequired,
        //    ISchemaRegistry schemaRegistry)
        //{
        //    var schema = schemaRegistry.GetOrRegister(apiParameterDescription.Type);

        //    return new BodyParameter { Name = name, Schema = schema, Required = isRequired };
        //}

        //private IParameter CreateNonBodyParameter(
        //    ApiParameterDescription apiParameterDescription,
        //    ParameterInfo parameterInfo,
        //    IEnumerable<object> customAttributes,
        //    string name,
        //    bool isRequired,
        //    ISchemaRegistry schemaRegistry)
        //{
        //    var location = ParameterLocationMap.ContainsKey(apiParameterDescription.Source)
        //        ? ParameterLocationMap[apiParameterDescription.Source]
        //        : "query";

        //    var nonBodyParam = new NonBodyParameter
        //    {
        //        Name = name,
        //        In = location,
        //        Required = (location == "path") ? true : isRequired,
        //    };

        //    if (apiParameterDescription.Type == null)
        //    {
        //        nonBodyParam.Type = "string";
        //    }
        //    else if (typeof(IFormFile).IsAssignableFrom(apiParameterDescription.Type))
        //    {
        //        nonBodyParam.Type = "file";
        //    }
        //    else
        //    {
        //        // Retrieve a Schema object for the type and copy common fields onto the parameter
        //        var schema = schemaRegistry.GetOrRegister(apiParameterDescription.Type);

        //        // NOTE: While this approach enables re-use of SchemaRegistry logic, it introduces complexity
        //        // and constraints elsewhere (see below) and needs to be refactored!

        //        if (schema.Ref != null)
        //        {
        //            // The registry created a referenced Schema that needs to be located. This means it's not neccessarily
        //            // exclusive to this parameter and so, we can't assign any parameter specific attributes or metadata.
        //            schema = schemaRegistry.Definitions[schema.Ref.Replace("#/definitions/", string.Empty)];
        //        }
        //        else
        //        {
        //            // It's a value Schema. This means it's exclusive to this parameter and so, we can assign
        //            // parameter specific attributes and metadata. Yep - it's hacky!
        //            schema.AssignAttributeMetadata(customAttributes);
        //            schema.Default = (parameterInfo != null && parameterInfo.IsOptional)
        //                ? parameterInfo.DefaultValue
        //                : null;
        //        }

        //        nonBodyParam.PopulateFrom(schema);
        //    }

        //    return nonBodyParam;
        //}

        //private IDictionary<string, Response> CreateResponses(
        //    ApiDescription apiDescription,
        //    ISchemaRegistry schemaRegistry)
        //{
        //    var supportedApiResponseTypes = apiDescription.SupportedResponseTypes
        //        .DefaultIfEmpty(new ApiResponseType { StatusCode = 200 });

        //    return supportedApiResponseTypes
        //        .ToDictionary(
        //            apiResponseType => apiResponseType.IsDefaultResponse() ? "default" : apiResponseType.StatusCode.ToString(),
        //            apiResponseType => CreateResponse(apiResponseType, schemaRegistry));
        //}

        //private Response CreateResponse(ApiResponseType apiResponseType, ISchemaRegistry schemaRegistry)
        //{
        //    var description = ResponseDescriptionMap
        //        .FirstOrDefault((entry) => Regex.IsMatch(apiResponseType.StatusCode.ToString(), entry.Key))
        //        .Value;

        //    return new Response
        //    {
        //        Description = description,
        //        Schema = (apiResponseType.Type != null && apiResponseType.Type != typeof(void))
        //            ? schemaRegistry.GetOrRegister(apiResponseType.Type)
        //            : null
        //    };
        //}

        private static Dictionary<BindingSource, ParameterLocation> ParameterLocationMap = new Dictionary<BindingSource, ParameterLocation>
        {
            { BindingSource.Query, ParameterLocation.Query },
            { BindingSource.ModelBinding, ParameterLocation.Query },
            { BindingSource.Header, ParameterLocation.Header },
            { BindingSource.Path, ParameterLocation.Path }
        };

        private static IEnumerable<BindingSource> FormBindingSources = new[] { BindingSource.Form, BindingSource.FormFile };

        private static IEnumerable<Type> RequiredAttributeTypes = new[] { typeof(BindRequiredAttribute), typeof(RequiredAttribute) };

        //private static readonly Dictionary<string, string> ResponseDescriptionMap = new Dictionary<string, string>
        //{
        //    { "1\\d{2}", "Information" },
        //    { "2\\d{2}", "Success" },
        //    { "3\\d{2}", "Redirect" },
        //    { "400", "Bad Request" },
        //    { "401", "Unauthorized" },
        //    { "403", "Forbidden" },
        //    { "404", "Not Found" },
        //    { "405", "Method Not Allowed" },
        //    { "406", "Not Acceptable" },
        //    { "408", "Request Timeout" },
        //    { "409", "Conflict" },
        //    { "4\\d{2}", "Client Error" },
        //    { "5\\d{2}", "Server Error" }
        //};

        private static readonly Dictionary<string, OperationType> OperationTypeMap = new Dictionary<string, OperationType>
        {
            { "GET", OperationType.Get },
            { "PUT", OperationType.Put },
            { "POST", OperationType.Post },
            { "DELETE", OperationType.Delete },
            { "OPTIONS", OperationType.Options },
            { "HEAD", OperationType.Head },
            { "PATCH", OperationType.Patch },
            { "TRACE", OperationType.Trace }
        };
    }
}
