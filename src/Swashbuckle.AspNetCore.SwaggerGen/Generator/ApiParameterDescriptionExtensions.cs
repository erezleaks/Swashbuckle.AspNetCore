using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Swashbuckle.AspNetCore.SwaggerGen
{
    public static class ApiParameterDescriptionExtensions
    {
        internal static void GetAdditionalMetadata(
            this ApiParameterDescription apiParameterDesc,
            ApiDescription apiDescription,
            out ParameterInfo parameterInfo,
            out PropertyInfo propertyInfo,
            out IEnumerable<object> parameterOrPropertyAttributes)
        {
            parameterInfo = null;
            propertyInfo = null;
            parameterOrPropertyAttributes = Enumerable.Empty<object>();

            if (apiParameterDesc.TryGetParameterInfo(apiDescription, out parameterInfo))
                parameterOrPropertyAttributes = parameterInfo.GetCustomAttributes(true);
            else if (apiParameterDesc.TryGetPropertyInfo(out propertyInfo))
                parameterOrPropertyAttributes = propertyInfo.GetCustomAttributes(true);
        }

        private static bool TryGetParameterInfo(
            this ApiParameterDescription apiParameterDesc,
            ApiDescription apiDescription,
            out ParameterInfo parameterInfo)
        {
            var controllerParameterDescriptor = apiDescription.ActionDescriptor.Parameters
                .OfType<ControllerParameterDescriptor>()
                .FirstOrDefault(descriptor =>
                {
                    return (apiParameterDesc.Name == descriptor.BindingInfo?.BinderModelName)
                        || (apiParameterDesc.Name == descriptor.Name);
                });

            parameterInfo = controllerParameterDescriptor?.ParameterInfo;

            return (parameterInfo != null);
        }

        private static bool TryGetPropertyInfo(
            this ApiParameterDescription apiParameterDesc,
            out PropertyInfo propertyInfo)
        {
            var modelMetadata = apiParameterDesc.ModelMetadata;

            propertyInfo = (modelMetadata?.ContainerType != null)
                ? modelMetadata.ContainerType.GetProperty(modelMetadata.PropertyName)
                : null;

            return (propertyInfo != null);
        }
    }
}