﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Microsoft.Azure.WebJobs.Script.WebHost.DependencyInjection
{
    /// <summary>
    /// An <see cref="IServiceProviderFactory{TContainerBuilder}"/> implementation that creates
    /// and populates a child scope that can be used as the <see cref="IServiceProvider"/>.
    /// </summary>
    public class JobHostScopedServiceProviderFactory : IServiceProviderFactory<IServiceCollection>
    {
        private readonly IServiceProvider _rootProvider;
        private readonly IServiceCollection _rootServices;
        private readonly IDependencyValidator _validator;
        private readonly ILogger _logger;

        public JobHostScopedServiceProviderFactory(IServiceProvider rootProvider, IServiceCollection rootServices, IDependencyValidator validator)
        {
            _rootProvider = rootProvider ?? throw new ArgumentNullException(nameof(rootProvider));
            _rootServices = rootServices ?? throw new ArgumentNullException(nameof(rootServices));
            _validator = validator ?? throw new ArgumentNullException(nameof(validator));
            _logger = ((ILogger)rootProvider.GetService<ILogger<JobHostScopedServiceProviderFactory>>()) ?? NullLogger.Instance;
        }

        public IServiceCollection CreateBuilder(IServiceCollection services)
        {
            return services;
        }

        /// <summary>
        /// This creates the service provider *and the end* of spinning up the JobHost.
        /// When we build the ScriptHost (<see cref="DefaultScriptHostBuilder.BuildHost(bool, bool)"/>),
        /// all services are fed in here (<paramref name="services"/>), and using that list we build
        /// a provider that has all of the base level services that we want to copy, then adds all of the
        /// SciptHost level services on top. It is not a proxying provider, we are copying the services
        /// references into that (rarely - e.g. startup and specialization) created ScriptHost layer scope.
        /// </summary>
        /// <param name="services">The ScriptHost services to add on top of the copied root services.</param>
        /// <returns>A provider containing the superset of base (application) level services and ScriptHost servics.</returns>
        /// <exception cref="HostInitializationException">If service validation fails (e.g. user touches something they shouldn't have.</exception>
        public IServiceProvider CreateServiceProvider(IServiceCollection services)
        {
            try
            {
                // Validating that a customer hasn't overridden things that they shouldn't
                _validator.Validate(services);
            }
            catch (InvalidHostServicesException ex)
            {
                // Log this to the WebHost's logger so we can track
                ILogger logger = _rootProvider.GetService<ILogger<DependencyValidator>>();
                logger.LogError(ex, "Invalid host services detected.");

                // rethrow to prevent host startup
                throw new HostInitializationException("Invalid host services detected.", ex);
            }

            ShimBreakingChanges(services, _logger);

            // Start from the root (web app level) as a base
            var jobHostServices = _rootProvider.CreateChildContainer(_rootServices);

            // ...and then add all the child services to this container
            foreach (var service in services)
            {
                jobHostServices.Add(service);
            }

            return jobHostServices.BuildServiceProvider();
        }

        private static void ShimBreakingChanges(IServiceCollection services, ILogger logger)
        {
            ShimActivatorUtilitiesConstructorAttributeTypes(services, logger);
            ShimLegacyNetWorkerMetadataProvider(services, logger);
        }

        /// <summary>
        /// Versions 1.3.0 and older of Microsoft.Azure.Functions.Worker.Sdk register a type with a constructor that
        /// takes a string parameter. This worked with DryIoc, but not with the new DI container.
        /// Shimming those types to provide backwards compatibility, but this should be removed in the future.
        /// </summary>
        /// <param name="services">The <see cref="ServiceCollection"/> to inspect and modify.</param>
        /// <param name="logger">The <see cref="ILogger"/> to log information.</param>
        private static void ShimLegacyNetWorkerMetadataProvider(IServiceCollection services, ILogger logger)
        {
            const string functionProviderTypeName = "Microsoft.Azure.WebJobs.Extensions.FunctionMetadataLoader.JsonFunctionProvider, Microsoft.Azure.WebJobs.Extensions.FunctionMetadataLoader, Version=1.0.0.0, Culture=neutral, PublicKeyToken=551316b6919f366c";
            const string jsonReaderTypeName = "Microsoft.Azure.WebJobs.Extensions.FunctionMetadataLoader.FunctionMetadataJsonReader, Microsoft.Azure.WebJobs.Extensions.FunctionMetadataLoader, Version=1.0.0.0, Culture=neutral, PublicKeyToken=551316b6919f366c";

            Type functionProviderType = Type.GetType(functionProviderTypeName);
            if (functionProviderType is null)
            {
                return;
            }

            Type jsonReaderType = Type.GetType(jsonReaderTypeName);
            var constructor = functionProviderType.GetConstructor([jsonReaderType, typeof(string)]);
            if (constructor is null)
            {
                return;
            }

            ServiceDescriptor descriptorToShim = null;
            foreach (ServiceDescriptor descriptor in services)
            {
                if (descriptor.ImplementationType == functionProviderType)
                {
                    logger.LogInformation("Shimming .NET Worker Function Provider constructor for {ImplementationType}.", descriptor.ImplementationType);
                    descriptorToShim = descriptor;
                    break;
                }
            }

            if (descriptorToShim is not null)
            {
                var newDescriptor = ServiceDescriptor.Describe(
                    descriptorToShim.ServiceType,
                    sp =>
                    {
                        var jsonReader = sp.GetRequiredService(jsonReaderType);
                        return constructor.Invoke([jsonReader, null]);
                    },
                    descriptorToShim.Lifetime);

                services.Remove(descriptorToShim);
                services.Add(newDescriptor);
            }
        }

        /// <summary>
        /// .NET 8 has a breaking change regarding <see cref="ActivatorUtilitiesConstructorAttribute"/> no longer functioning as expected.
        /// We have some known extension types which are impacted by this. To avoid a regression, we are manually shimming those types.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="logger">The <see cref="ILogger"/> to log information.</param>
        private static void ShimActivatorUtilitiesConstructorAttributeTypes(IServiceCollection services, ILogger logger)
        {
            Dictionary<ServiceDescriptor, ServiceDescriptor> toReplace = null;
            static bool HasPreferredCtor(Type type)
            {
                foreach (ConstructorInfo c in type.GetConstructors())
                {
                    if (c.IsDefined(typeof(ActivatorUtilitiesConstructorAttribute), false))
                    {
                        return true;
                    }
                }

                return false;
            }

            bool TryCreateReplacement(ServiceDescriptor descriptor, out ServiceDescriptor replacement)
            {
                if (!HasPreferredCtor(descriptor.ImplementationType))
                {
                    replacement = null;
                    return false;
                }

                logger.LogInformation("Shimming DI constructor for {ImplementationType}.", descriptor.ImplementationType);
                ObjectFactory factory = ActivatorUtilities.CreateFactory(descriptor.ImplementationType, Type.EmptyTypes);

                replacement = ServiceDescriptor.Describe(
                    descriptor.ServiceType, sp => factory.Invoke(sp, Type.EmptyTypes), descriptor.Lifetime);
                return true;
            }

            // NetheriteProviderFactory uses ActivatorUtilitiesConstructorAttribute. We will replace this implementation with an explicit delegate.
            Type netheriteProviderFactory = Type.GetType("DurableTask.Netherite.AzureFunctions.NetheriteProviderFactory, DurableTask.Netherite.AzureFunctions, Version=1.0.0.0, Culture=neutral, PublicKeyToken=ef8c4135b1b4225a");
            foreach (ServiceDescriptor descriptor in services)
            {
                if (netheriteProviderFactory is not null
                    && descriptor.ImplementationType == netheriteProviderFactory
                    && TryCreateReplacement(descriptor, out ServiceDescriptor replacement))
                {
                    toReplace ??= new Dictionary<ServiceDescriptor, ServiceDescriptor>();
                    toReplace.Add(descriptor, replacement);
                }
            }

            if (toReplace is null)
            {
                return;
            }

            foreach ((ServiceDescriptor key, ServiceDescriptor value) in toReplace)
            {
                services.Remove(key);
                services.Add(value);
            }
        }
    }
}
