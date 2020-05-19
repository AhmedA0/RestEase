﻿using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RestEase.Implementation.Analysis;
using RestEase.Implementation.Emission;

namespace RestEase.Implementation
{
    internal class ImplementationGenerator
    {
        private static readonly Regex pathParamMatch = new Regex(@"\{(.+?)\}");

        private readonly TypeModel typeModel;
        private readonly Emitter emitter;
        private readonly DiagnosticReporter diagnostics;

        public ImplementationGenerator(TypeModel typeModel, Emitter emitter, DiagnosticReporter diagnosticReporter)
        {
            this.typeModel = typeModel;
            this.emitter = emitter;
            this.diagnostics = diagnosticReporter;
        }

        public EmittedType Generate()
        {
            foreach (var header in this.typeModel.HeaderAttributes)
            {
                if (header.Attribute.Value == null)
                {
                    this.diagnostics.ReportHeaderOnInterfaceMustHaveValue(header);
                }
                if (header.Attribute.Name.Contains(":"))
                {
                    this.diagnostics.ReportHeaderOnInterfaceMustNotHaveColonInName(header);
                }
            }

            foreach (var attribute in this.typeModel.AllowAnyStatusCodeAttributes)
            {
                if (!attribute.IsDefinedOn(this.typeModel))
                {
                    this.diagnostics.ReportAllowAnyStatisCodeAttributeNotAllowedOnParentInterface(attribute);
                }
            }

            foreach (var @event in this.typeModel.Events)
            {
                this.diagnostics.ReportEventNotAllowed(@event);
            }

            var typeEmitter = this.emitter.EmitType(this.typeModel);

            this.ValidatePathProperties();
            var emittedProperties = this.GenerateProperties(typeEmitter);
            this.GenerateMethods(typeEmitter, emittedProperties);
            return typeEmitter.Generate();
        }

        private List<EmittedProperty> GenerateProperties(TypeEmitter typeEmitter)
        {
            var emittedProperties = new List<EmittedProperty>(this.typeModel.Properties.Count);

            bool hasRequester = false;

            foreach (var property in this.typeModel.Properties)
            { 
                var attributes = property.GetAllSetAttributes().ToList();
                if (property.IsRequester)
                {
                    if (hasRequester)
                    {
                        this.diagnostics.ReportMultipleRequesterPropertiesNotAllowed(property);
                    }

                    if (attributes.Count > 0)
                    {
                        this.diagnostics.ReportRequesterPropertyMustHaveZeroAttributes(property, attributes);
                    }

                    if (!property.HasGetter || property.HasSetter)
                    {
                        this.diagnostics.ReportPropertyMustBeReadOnly(property);
                    }

                    typeEmitter.EmitRequesterProperty(property);

                    hasRequester = true;
                }
                else
                {
                    if (attributes.Count != 1)
                    {
                        this.diagnostics.ReportPropertyMustHaveOneAttribute(property);
                    }
                    
                    if (!property.HasGetter || !property.HasSetter)
                    {
                        this.diagnostics.ReportPropertyMustBeReadWrite(property);
                    }

                    if (property.HeaderAttribute != null)
                    {
                        var headerAttribute = property.HeaderAttribute.Attribute;
                        if (headerAttribute.Value != null && !property.IsNullable)
                        {
                            this.diagnostics.ReportHeaderPropertyWithValueMustBeNullable(property);
                        }
                        if (headerAttribute.Name.Contains(":"))
                        {
                            this.diagnostics.ReportHeaderPropertyNameMustContainColon(property);
                        }
                    }

                    emittedProperties.Add(typeEmitter.EmitProperty(property));
                }
            }

            return emittedProperties;
        }

        private void GenerateMethods(TypeEmitter typeEmitter, List<EmittedProperty> emittedProperties)
        {
            foreach (var method in this.typeModel.Methods)
            {
                if (method.IsDisposeMethod)
                {
                    typeEmitter.EmitDisposeMethod(method);
                }
                else
                {
                    this.GenerateMethod(typeEmitter, emittedProperties, method);
                }
            }
        }

        private void GenerateMethod(TypeEmitter typeEmitter, List<EmittedProperty> emittedProperties, MethodModel method)
        {
            var methodEmitter = typeEmitter.EmitMethod(method);
            var serializationMethods = new ResolvedSerializationMethods(this.typeModel.SerializationMethodsAttribute?.Attribute, method.SerializationMethodsAttribute?.Attribute);
            if (method.RequestAttribute == null)
            {
                this.diagnostics.ReportMethodMustHaveRequestAttribute(method);
            }
            else
            {
                string? path = method.RequestAttribute.Attribute.Path;
                this.ValidatePathParams(method, path);
                this.ValidateHttpRequestMessageParameters(method);

                methodEmitter.EmitRequestInfoCreation(method.RequestAttribute.Attribute);

                var resolvedAllowAnyStatusCode = method.AllowAnyStatusCodeAttribute ?? this.typeModel.TypeAllowAnyStatusCodeAttribute;
                if (resolvedAllowAnyStatusCode?.Attribute.AllowAnyStatusCode == true)
                {
                    methodEmitter.EmitSetAllowAnyStatusCode();
                }

                if (this.typeModel.BasePathAttribute?.Attribute.BasePath != null)
                {
                    methodEmitter.EmitSetBasePath(this.typeModel.BasePathAttribute.Attribute.BasePath);
                }

                this.GenerateMethodProperties(methodEmitter, emittedProperties, serializationMethods);

                foreach (var methodHeader in method.HeaderAttributes)
                {
                    if (methodHeader.Attribute.Name.Contains(":"))
                    {
                        this.diagnostics.ReportHeaderOnMethodMustNotHaveColonInName(method, methodHeader);
                    }

                    methodEmitter.EmitAddMethodHeader(methodHeader);
                }

                this.GenerateMethodParameters(methodEmitter, method, serializationMethods);

                if (!methodEmitter.TryEmitRequestMethodInvocation())
                {
                    this.diagnostics.ReportMethodMustHaveValidReturnType(method);
                }
            }
        }

        private void GenerateMethodProperties(MethodEmitter methodEmitter, List<EmittedProperty> emittedProperties, ResolvedSerializationMethods serializationMethods)
        {
            foreach (var property in emittedProperties)
            {
                // We've already validated these
                if (property.PropertyModel.HeaderAttribute != null)
                {
                    methodEmitter.EmitAddHeaderProperty(property);
                }
                else if (property.PropertyModel.PathAttribute != null)
                {
                    methodEmitter.EmitAddPathProperty(
                        property,
                        serializationMethods.ResolvePath(property.PropertyModel.PathAttribute.Attribute.SerializationMethod));
                }
                else if (property.PropertyModel.QueryAttribute != null)
                {
                    methodEmitter.EmitAddQueryProperty(
                        property,
                        serializationMethods.ResolveQuery(property.PropertyModel.QueryAttribute.Attribute.SerializationMethod));
                }
                else if (property.PropertyModel.HttpRequestMessagePropertyAttribute != null)
                {
                    methodEmitter.EmitAddHttpRequestMessagePropertyProperty(property);
                }
            }
        }

        private void GenerateMethodParameters(MethodEmitter methodEmitter, MethodModel method, ResolvedSerializationMethods serializationMethods)
        {
            bool hasCancellationToken = false;
            bool hasBodyParameter = false;
            foreach (var parameter in method.Parameters)
            {
                var attributes = parameter.GetAllSetAttributes().ToList();

                if (parameter.IsCancellationToken)
                {
                    if (hasCancellationToken)
                    {
                        this.diagnostics.ReportMultipleCancellationTokenParameters(method, parameter);
                    }
                    if (attributes.Count > 0)
                    {
                        this.diagnostics.ReportCancellationTokenMustHaveZeroAttributes(method, parameter);
                    }

                    methodEmitter.EmitSetCancellationToken(parameter);

                    hasCancellationToken = true;
                }
                else
                {
                    if (attributes.Count > 1)
                    {
                        this.diagnostics.ReportParameterMustHaveZeroOrOneAttributes(method, parameter, attributes);
                    }

                    if (parameter.HeaderAttribute != null)
                    {
                        if (parameter.HeaderAttribute.Attribute.Value != null)
                        {
                            this.diagnostics.ReportHeaderParameterMustNotHaveValue(method, parameter);
                        }
                        if (parameter.HeaderAttribute.Attribute.Name.Contains(":"))
                        {
                            this.diagnostics.ReportHeaderParameterMustNotHaveColonInName(method, parameter);
                        }

                        methodEmitter.EmitAddHeaderParameter(parameter);
                    }
                    else if (parameter.PathAttribute != null)
                    {
                        methodEmitter.EmitAddPathParameter(
                            parameter,
                            serializationMethods.ResolvePath(parameter.PathAttribute.Attribute.SerializationMethod));
                    }
                    else if (parameter.QueryAttribute != null)
                    {
                        methodEmitter.EmitAddQueryParameter(
                            parameter,
                            serializationMethods.ResolveQuery(parameter.QueryAttribute.Attribute.SerializationMethod));
                    }
                    else if (parameter.HttpRequestMessagePropertyAttribute != null)
                    {
                        methodEmitter.EmitAddHttpRequestMessagePropertyParameter(parameter);
                    }
                    else if (parameter.RawQueryStringAttribute != null)
                    {
                        methodEmitter.EmitAddRawQueryStringParameter(parameter);
                    }
                    else if (parameter.QueryMapAttribute != null)
                    {
                        if (!methodEmitter.TryEmitAddQueryMapParameter(parameter, serializationMethods.ResolveQuery(parameter.QueryMapAttribute.Attribute.SerializationMethod)))
                        {
                            this.diagnostics.ReportQueryMapParameterIsNotADictionary(method, parameter);
                        }
                    }
                    else if (parameter.BodyAttribute != null)
                    {
                        if (hasBodyParameter)
                        {
                            this.diagnostics.ReportMultipleBodyParameters(method, parameter);
                        }

                        methodEmitter.EmitSetBodyParameter(
                            parameter,
                            serializationMethods.ResolveBody(parameter.BodyAttribute.Attribute.SerializationMethod));

                        hasBodyParameter = true;
                    }
                    else
                    {
                        methodEmitter.EmitAddQueryParameter(parameter, serializationMethods.ResolveQuery(QuerySerializationMethod.Default));
                    }
                }
            }
        }

        private void ValidatePathProperties()
        {
            var pathProperties = this.typeModel.Properties.Where(x => x.PathAttribute != null);

            // Check that there are no duplicate param names in the properties
            var duplicateProperties = pathProperties.GroupBy(x => x.PathAttributeName!).Where(x => x.Count() > 1);
            foreach (var properties in duplicateProperties)
            {
                this.diagnostics.ReportMultiplePathPropertiesForKey(properties.Key, properties);
            }

            string? basePath = this.typeModel.BasePathAttribute?.Attribute.BasePath;
            if (basePath != null)
            {
                // Check that each placeholder in the base path has a matching path property, and vice versa.
                // We don't consider path parameters here.
                var placeholders = pathParamMatch.Matches(basePath).Cast<Match>().Select(x => x.Groups[1].Value).ToList();

                var missingParams = placeholders.Except(pathProperties.Select(x => x.PathAttributeName!));
                foreach (string missingParam in missingParams)
                {
                    this.diagnostics.ReportMissingPathPropertyForBasePathPlaceholder(missingParam, basePath);
                }
            }
        }

        private void ValidatePathParams(MethodModel method, string? path)
        {
            if (path == null)
                path = string.Empty;

            var pathParams = method.Parameters.Where(x => x.PathAttribute != null).ToList();

            // Check that there are no duplicate param names in the attributes
            var duplicateParams = pathParams.GroupBy(x => x.PathAttributeName!).Where(x => x.Count() > 1);
            foreach (var @params in duplicateParams)
            {
                this.diagnostics.ReportMultiplePathParametersForKey(method, @params.Key, @params);
            }

            // Check that each placeholder has a matching attribute, and vice versa
            // We allow a property param to fill in for a missing path param, but we allow them to duplicate
            // each other (the path param takes precedence), and allow a property param which doesn't have a placeholder.
            var placeholders = pathParamMatch.Matches(path).Cast<Match>().Select(x => x.Groups[1].Value).ToList();

            var missingParams = placeholders
                .Except(pathParams.Select(x => x.PathAttributeName!).Concat(this.typeModel.Properties.Select(x => x.PathAttributeName!)));
            foreach (string missingParam in missingParams)
            {
                this.diagnostics.ReportMissingPathPropertyOrParameterForPlaceholder(method, missingParam);
            }

            var missingPlaceholders = pathParams.Select(x => x.PathAttributeName!).Except(placeholders);
            foreach (string? missingPlaceholder in missingPlaceholders)
            {
                this.diagnostics.ReportMissingPlaceholderForPathParameter(method, missingPlaceholder);
            }
        }

        private void ValidateHttpRequestMessageParameters(MethodModel method)
        {
            // Check that there are no duplicate param names in the attributes
            var requestParams = method.Parameters.Where(x => x.HttpRequestMessagePropertyAttribute != null);
            var duplicateParams = requestParams
                .GroupBy(x => x.HttpRequestMessagePropertyAttributeKey!)
                .Where(x => x.Count() > 1);
            foreach (var @params in duplicateParams)
            {
                this.diagnostics.ReportDuplicateHttpRequestMessagePropertyKey(method, @params.Key, @params);
            }
        }
    }
}