﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NuGet.Commands;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.ProjectModel;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;

namespace NuGet.CommandLine.XPlat
{
    public class AddPackageReferenceCommandRunner : IAddPackageReferenceCommandRunner
    {
        private static string NUGET_RESTORE_MSBUILD_VERBOSITY = "NUGET_RESTORE_MSBUILD_VERBOSITY";
        private static int MSBUILD_WAIT_TIME = 2 * 60 * 1000; // 2 minutes in milliseconds

        public int ExecuteCommand(PackageReferenceArgs packageReferenceArgs, MSBuildAPIUtility msBuild)
        {
            packageReferenceArgs.Logger.LogInformation(string.Format(CultureInfo.CurrentCulture,
                Strings.Info_AddPkgAddingReference,
                packageReferenceArgs.PackageDependency.Id,
                packageReferenceArgs.ProjectPath));

            if (packageReferenceArgs.NoRestore)
            {
                packageReferenceArgs.Logger.LogWarning(string.Format(CultureInfo.CurrentCulture,
                    Strings.Warn_AddPkgWithoutRestore));

                msBuild.AddPackageReference(packageReferenceArgs.ProjectPath, packageReferenceArgs.PackageDependency);
                return 0;
            }

            using (var dgFilePath = new TempFile(".dg"))
            {
                // 1. Get project dg file
                packageReferenceArgs.Logger.LogInformation("Generating project Dependency Graph");
                var dgSpec = GetProjectDependencyGraphAsync(packageReferenceArgs, dgFilePath, timeOut: MSBUILD_WAIT_TIME).Result;
                packageReferenceArgs.Logger.LogInformation("Project Dependency Graph Generated");
                var projectName = dgSpec.Restore.FirstOrDefault();
                var originalPackageSpec = dgSpec.GetProjectSpec(projectName);

                // Create a copy to avoid modifying the original spec which may be shared.
                var updatedPackageSpec = originalPackageSpec.Clone();
                PackageSpecOperations.AddOrUpdateDependency(updatedPackageSpec, packageReferenceArgs.PackageDependency);

                var updatedDgSpec = dgSpec.WithReplacedSpec(updatedPackageSpec).WithoutRestores();
                updatedDgSpec.AddRestore(updatedPackageSpec.RestoreMetadata.ProjectUniqueName);

                // 2. Run Restore Preview
                packageReferenceArgs.Logger.LogInformation("Running Restore preview");
                var restorePreviewResult = PreviewAddPackageReference(packageReferenceArgs, updatedDgSpec, updatedPackageSpec).Result;
                packageReferenceArgs.Logger.LogInformation("Restore Review completed");

                // 3. Process Restore Result
                var compatibleFrameworks = new HashSet<NuGetFramework>(
                    restorePreviewResult
                    .Result
                    .CompatibilityCheckResults
                    .Where(t => t.Success)
                    .Select(t => t.Graph.Framework));

                if (packageReferenceArgs.Frameworks != null && packageReferenceArgs.Frameworks.Count() > 0)
                {
                    // If the user has specified frameworks then we intersect that with the compatible frameworks.
                    var userSpecifiedFrameworks = new HashSet<NuGetFramework>(
                        packageReferenceArgs
                        .Frameworks
                        .Select(f => NuGetFramework.Parse(f)));

                    compatibleFrameworks.IntersectWith(userSpecifiedFrameworks);
                }

                // 4. Write to Project
                if (compatibleFrameworks.Count == 0)
                {
                    // Package is compatible with none of the project TFMs
                    // Do not add a package reference, throw appropriate error
                    packageReferenceArgs.Logger.LogInformation(string.Format(CultureInfo.CurrentCulture,
                        Strings.Error_AddPkgIncompatibleWithAllFrameworks,
                        packageReferenceArgs.PackageDependency.Id,
                        packageReferenceArgs.ProjectPath));
                    return 1;
                }
                else if (compatibleFrameworks.Count == restorePreviewResult.Result.CompatibilityCheckResults.Count())
                {
                    // Package is compatible with all the project TFMs
                    // Add an unconditional package reference to the project
                    packageReferenceArgs.Logger.LogInformation(string.Format(CultureInfo.CurrentCulture,
                        Strings.Info_AddPkgCompatibleWithAllFrameworks,
                        packageReferenceArgs.PackageDependency.Id,
                        packageReferenceArgs.ProjectPath));

                    msBuild.AddPackageReference(packageReferenceArgs.ProjectPath, packageReferenceArgs.PackageDependency);
                }
                else
                {
                    // Package is compatible with some of the project TFMs
                    // Add conditional package references to the project for the compatible TFMs
                    var compatibleOriginalFrameworks = originalPackageSpec.RestoreMetadata
                        .OriginalTargetFrameworks
                        .Where(s => compatibleFrameworks.Contains(NuGetFramework.Parse(s)));
                    packageReferenceArgs.Logger.LogInformation(string.Format(CultureInfo.CurrentCulture,
                        Strings.Info_AddPkgCompatibleWithSubsetFrameworks,
                        packageReferenceArgs.PackageDependency.Id,
                        packageReferenceArgs.ProjectPath));

                    msBuild.AddPackageReferencePerTFM(packageReferenceArgs.ProjectPath, packageReferenceArgs.PackageDependency, compatibleOriginalFrameworks);
                }
            }
            return 0;
        }

        private async Task<RestoreResultPair> PreviewAddPackageReference(PackageReferenceArgs packageReferenceArgs, DependencyGraphSpec dgSpec, PackageSpec originalPackageSpec)
        {
            // Set user agent and connection settings.
            XPlatUtility.ConfigureProtocol();

            var providerCache = new RestoreCommandProvidersCache();

            using (var cacheContext = new SourceCacheContext())
            {
                cacheContext.NoCache = false;
                cacheContext.IgnoreFailedSources = true;

                // Pre-loaded request provider containing the graph file
                var providers = new List<IPreLoadedRestoreRequestProvider>();

                // Create a copy to avoid modifying the original spec which may be shared.
                var updatedPackageSpec = originalPackageSpec.Clone();

                PackageSpecOperations.AddOrUpdateDependency(updatedPackageSpec, packageReferenceArgs.PackageDependency);

                providers.Add(new DependencyGraphSpecRequestProvider(providerCache, dgSpec));

                var restoreContext = new RestoreArgs()
                {
                    CacheContext = cacheContext,
                    LockFileVersion = LockFileFormat.Version,
                    Log = packageReferenceArgs.Logger,
                    MachineWideSettings = new XPlatMachineWideSetting(),
                    GlobalPackagesFolder = packageReferenceArgs.PackageDirectory,
                    PreLoadedRequestProviders = providers,
                    Sources = packageReferenceArgs.Sources.ToList()
                };

                // Generate Restore Requests. There will always be 1 request here since we are restoring for 1 project.
                var restoreRequests = await RestoreRunner.GetRequests(restoreContext);

                // Run restore without commit. This will always return 1 Result pair since we are restoring for 1 request.
                var restoreResult = await RestoreRunner.RunWithoutCommit(restoreRequests, restoreContext);

                return restoreResult.Single();
            }
        }

        private static async Task<DependencyGraphSpec> GetProjectDependencyGraphAsync(
            PackageReferenceArgs packageReferenceArgs,
            string dgFilePath,
            int timeOut)
        {
            var dotnetLocation = packageReferenceArgs.DotnetPath;

            if (!File.Exists(dotnetLocation))
            {
                throw new Exception(
                    string.Format(CultureInfo.CurrentCulture, Strings.Error_DotnetNotFound));
            }
            var argumentBuilder = new StringBuilder($@" /t:GenerateRestoreGraphFile");

            // Set the msbuild verbosity level if specified
            var msbuildVerbosity = Environment.GetEnvironmentVariable(NUGET_RESTORE_MSBUILD_VERBOSITY);

            if (string.IsNullOrEmpty(msbuildVerbosity))
            {
                argumentBuilder.Append(" /v:q ");
            }
            else
            {
                argumentBuilder.Append($" /v:{msbuildVerbosity} ");
            }

            // Pass dg file output path
            argumentBuilder.Append(" /p:RestoreGraphOutputPath=");
            AppendQuoted(argumentBuilder, dgFilePath);

            packageReferenceArgs.Logger.LogInformation($"{dotnetLocation} msbuild {argumentBuilder.ToString()}");

            var processStartInfo = new ProcessStartInfo
            {
                UseShellExecute = false,
                FileName = dotnetLocation,
                WorkingDirectory = Path.GetDirectoryName(packageReferenceArgs.ProjectPath),
                Arguments = $"msbuild {argumentBuilder.ToString()}",
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            packageReferenceArgs.Logger.LogDebug($"{processStartInfo.FileName} {processStartInfo.Arguments}");

            using (var process = Process.Start(processStartInfo))
            {
                var outputs = new ConcurrentQueue<string>();
                var outputTask = ConsumeStreamReaderAsync(process.StandardOutput, outputs);
                var errorTask = ConsumeStreamReaderAsync(process.StandardError, outputs);

                var finished = process.WaitForExit(timeOut);
                if (!finished)
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (Exception ex)
                    {
                        throw new Exception(string.Format(CultureInfo.CurrentCulture, Strings.Error_CannotKillDotnetMsBuild) + " : " +
                            ex.Message,
                            ex);
                    }

                    throw new Exception(string.Format(CultureInfo.CurrentCulture, Strings.Error_DotnetMsBuildTimedOut));
                }

                if (process.ExitCode != 0)
                {
                    await errorTask;
                    await outputTask;
                    LogQueue(outputs, packageReferenceArgs.Logger);
                    throw new Exception(string.Format(CultureInfo.CurrentCulture, Strings.Error_GenerateDGSpecTaskFailed));
                }
            }

            DependencyGraphSpec spec = null;

            if (File.Exists(dgFilePath))
            {
                spec = DependencyGraphSpec.Load(dgFilePath);
                File.Delete(dgFilePath);
            }
            else
            {
                spec = new DependencyGraphSpec();
            }

            return spec;
        }

        private static void LogQueue(ConcurrentQueue<string> outputs, ILogger logger)
        {
            foreach (var line in outputs)
            {
                logger.LogError(line);
            }
        }

        private static void AppendQuoted(StringBuilder builder, string targetPath)
        {
            builder
                .Append('"')
                .Append(targetPath)
                .Append('"');
        }

        private static async Task ConsumeStreamReaderAsync(StreamReader reader, ConcurrentQueue<string> lines)
        {
            await Task.Yield();

            string line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                lines.Enqueue(line);
            }
        }
    }
}