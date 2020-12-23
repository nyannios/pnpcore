﻿using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PnP.Core.Services;
using PnP.Core.Test.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PnP.Core.Test.Services
{
    /// <summary>
    /// Test context factory, delivering PnPContext objects that can be used in testing (with Mocking/Recording enabled)
    /// </summary>
    public class TestPnPContextFactory : PnPContextFactory
    {
        /// <summary>
        /// Generate a context configured for mocking mode or recording mode
        /// </summary>
        public bool Mocking { get; set; } = true;

        /// <summary>
        /// Context id, useful when a test contains multiple context creations
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Name of the test
        /// </summary>
        public string TestName { get; set; }

        /// <summary>
        /// Source file of the test, will be used to determine the path for storing test files
        /// </summary>
        public string SourceFilePath { get; set; }

        /// <summary>
        /// Generate the .request and .debug files, can be handy for debugging
        /// </summary>
        public bool GenerateTestMockingDebugFiles { get; set; }

        /// <summary>
        /// Urls's used by the test cases
        /// </summary>
        public Dictionary<string, Uri> TestUris { get; set; }

        public TestPnPContextFactory(
            ILogger<PnPContext> logger,
            SharePointRestClient sharePointRestClient,
            MicrosoftGraphClient microsoftGraphClient,
            IOptions<PnPContextFactoryOptions> contextOptions,
            IOptions<PnPGlobalSettingsOptions> globalOptions) : base(logger, sharePointRestClient, microsoftGraphClient, contextOptions, globalOptions)
        {
            if (TelemetryManager != null && !TestCommon.RunningInGitHubWorkflow())
            {
                // Send telemetry to the test Azure AppInsights instance
                TelemetryManager.TelemetryClient.InstrumentationKey = "6073339d-9e70-4004-9ff7-1345316ade97";
            }
        }

        public override PnPContext Create(string name)
        {
            return CreateAsync(name).GetAwaiter().GetResult();
        }

        public async override Task<PnPContext> CreateAsync(string name)
        {
            // Search for the provided configuration
            var configuration = ContextOptions.Configurations.FirstOrDefault(c => c.Name == name);
            if (configuration == null)
            {
                throw new ClientException(ErrorType.ConfigurationError,
                    string.Format(PnPCoreResources.Exception_ConfigurationError_InvalidPnPContextConfigurationName, name));
            }

            return await CreateAsync(configuration.SiteUrl, configuration.AuthenticationProvider).ConfigureAwait(false);
        }

        public override PnPContext Create(Uri url, IAuthenticationProvider authenticationProvider)
        {
            return CreateAsync(url, authenticationProvider).GetAwaiter().GetResult();
        }

        public async override Task<PnPContext> CreateAsync(Uri url, IAuthenticationProvider authenticationProvider)
        {
            // Use the provided settings to create a new instance of SPOContext
            var context = new PnPContext(Log, authenticationProvider, SharePointRestClient, MicrosoftGraphClient, ContextOptions, GlobalOptions, TelemetryManager)
            {
                Uri = url
            };

            ConfigurePnPContextForTesting(ref context);

            // Perform context initialization
            await InitializeContextAsync(context).ConfigureAwait(false);

            // Configure the global Microsoft Graph settings
            context.GraphFirst = ContextOptions.GraphFirst;
            context.GraphCanUseBeta = ContextOptions.GraphCanUseBeta;
            context.GraphAlwaysUseBeta = ContextOptions.GraphAlwaysUseBeta;

            await ConfigureTelemetry(context).ConfigureAwait(false);

            return context;
        }

        public override PnPContext Create(Uri url)
        {
            return CreateAsync(url).GetAwaiter().GetResult();
        }

        public async override Task<PnPContext> CreateAsync(Uri url)
        {
            // Use the default settings to create a new instance of SPOContext
            return await CreateAsync(url, ContextOptions.DefaultAuthenticationProvider).ConfigureAwait(false);
        }

        public override PnPContext Create(Guid groupId, IAuthenticationProvider authenticationProvider)
        {
            return CreateAsync(groupId, authenticationProvider).GetAwaiter().GetResult();
        }

        public async override Task<PnPContext> CreateAsync(Guid groupId, IAuthenticationProvider authenticationProvider)
        {
            var context = new PnPContext(Log, authenticationProvider, SharePointRestClient, MicrosoftGraphClient, ContextOptions, GlobalOptions, TelemetryManager);

            ConfigurePnPContextForTesting(ref context);

            await ConfigureForGroup(context, groupId).ConfigureAwait(false);

            return context;
        }

        public override PnPContext Create(Guid groupId)
        {
            return CreateAsync(groupId).GetAwaiter().GetResult();
        }

        public async override Task<PnPContext> CreateAsync(Guid groupId)
        {
            var context = new PnPContext(Log, ContextOptions.DefaultAuthenticationProvider, SharePointRestClient, MicrosoftGraphClient, ContextOptions, GlobalOptions, TelemetryManager);

            ConfigurePnPContextForTesting(ref context);

            await ConfigureForGroup(context, groupId).ConfigureAwait(false);

            // Perform context initialization
            await InitializeContextAsync(context).ConfigureAwait(false);

            return context;
        }

        private void ConfigurePnPContextForTesting(ref PnPContext context)
        {
            if (Mocking)
            {
                context.SetMockMode(Id, TestName, SourceFilePath, GenerateTestMockingDebugFiles, TestUris);
            }
            else
            {
                context.SetRecordingMode(Id, TestName, SourceFilePath, GenerateTestMockingDebugFiles, TestUris);
            }
        }
    }
}
