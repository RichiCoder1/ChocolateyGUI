﻿// --------------------------------------------------------------------------------------------------------------------
// <copyright company="Chocolatey" file="PowerShellChocolateyPackageService.cs">
//   Copyright 2014 - Present Rob Reynolds, the maintainers of Chocolatey, and RealDimensions Software, LLC
// </copyright>
// --------------------------------------------------------------------------------------------------------------------

namespace ChocolateyGui.Services
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.IO;
    using System.Linq;
    using System.Management.Automation;
    using System.Management.Automation.Runspaces;
    using System.Threading.Tasks;
    using ChocolateyGui.Controls;
    using ChocolateyGui.Enums;
    using ChocolateyGui.Models;
    using ChocolateyGui.Providers;
    using ChocolateyGui.ViewModels.Items;

    public class PowerShellChocolateyPackageService : BasePackageService, IChocolateyPackageService
    {
        /// <summary>
        /// The PowerShell runspace for this service.
        /// </summary>
        private readonly Runspace _runspace;

        public PowerShellChocolateyPackageService(IProgressService progressService, Func<Type, ILogService> logServiceFunc, IChocolateyConfigurationProvider chocolateyConfigurationProvider)
            : base(progressService, logServiceFunc, chocolateyConfigurationProvider)
        {
            this._runspace = RunspaceFactory.CreateRunspace(new ChocolateyHost(this.ProgressService));
            this._runspace.Open();
        }

        /// <summary>
        /// Retrieves the currently installed packages.
        /// If the package list is cached, retrieve it from there.
        /// Else, scan the file system for packages and pull the appropriate information from there.
        /// </summary>
        /// <param name="force">Forces a cache reset.</param>
        /// <returns>List of currently installed packages.</returns>
        public async Task<IEnumerable<IPackageViewModel>> GetInstalledPackages(bool force = false)
        {
            // Ensure that we only retrieve the packages one at a to refresh the Cache.
            using (await this.GetInstalledLock.LockAsync())
            {
                ICollection<IPackageViewModel> packages;
                if (!force)
                {
                    packages = BasePackageService.CachedPackages;
                    if (packages != null)
                    {
                        return packages;
                    }
                }

                await this.ProgressService.StartLoading("Chocolatey Service");
                this.ProgressService.WriteMessage("Retrieving installed packages...");
                var chocoPath = this.ChocolateyConfigurationProvider.ChocolateyInstall;
                if (string.IsNullOrWhiteSpace(chocoPath) || !Directory.Exists(chocoPath))
                {
                    throw new InvalidDataException(
                        "Invalid Chocolatey Path. Check that chocolateyInstall is correct in the app.config.");
                }

                var libPath = Path.Combine(chocoPath, "lib");

                var chocoPackageList = (await this.RunIndirectChocolateyCommand("list -lo", false))
                    .Where(p => PackageRegex.IsMatch(p.ToString()))
                    .Select(p => PackageRegex.Match(p.ToString()))
                    .ToDictionary(m => m.Groups["Name"].Value, m => new SemanticVersion(m.Groups["VersionString"].Value));

                packages = new List<IPackageViewModel>();

                await this.EnumerateLocalPackagesAndSetCache(packages, chocoPackageList, libPath);

                await this.ProgressService.StopLoading();
                return packages;
            }
        }

        public async Task InstallPackage(string id, SemanticVersion version = null, Uri source = null, bool force = false)
        {
            await this.ProgressService.StartLoading(string.Format("Installing {0}...", id));
            this.ProgressService.WriteMessage("Building chocolatey command...");
            var arguments = new Dictionary<string, object> { { "command", "install" }, { "packageNames", id } };

            if (version != null)
            {
                arguments.Add("version", version.ToString());
            }

            if (source != null)
            {
                arguments.Add("source", source.ToString());
            }

            if (force)
            {
                arguments.Add("force", true);
            }

            await this.ExecutePackageCommand(arguments);

            var newPackage =
                (await this.GetInstalledPackages()).OrderByDescending(p => p.Version)
                    .FirstOrDefault(
                        p =>
                        string.Compare(p.Id, id, StringComparison.OrdinalIgnoreCase) == 0
                        && (version == null || version == p.Version));

            this.UpdatePackageLists(id, source, newPackage, version);
        }
        
        public async Task UninstallPackage(string id, SemanticVersion version, bool force = false)
        {
            this.StartProgressDialog("Uninstalling", "Building chocolatey command...", id);

            var arguments = new Dictionary<string, object>
                                {
                                    { "command", "uninstall" },
                                    { "version", version.ToString() },
                                    { "packageNames", id }
                                };

            await this.ExecutePackageCommand(arguments);

            this.RemovePackageEntry(id, version);
            this.NotifyPackagesChanged(PackagesChangedEventType.Uninstalled, id, version.ToString());
            await this.ProgressService.StopLoading();
        }

        public async Task UpdatePackage(string id, Uri source = null)
        {
            await this.ProgressService.StartLoading(string.Format("Updating {0}...", id));
            this.ProgressService.WriteMessage("Building chocolatey command...");
            var currentPackages = this.PackageConfigEntries().Where(p => string.Compare(p.Id, id, StringComparison.OrdinalIgnoreCase) == 0).ToList();

            var arguments = new Dictionary<string, object> { { "command", "update" }, { "packageNames", id } };

            await this.ExecutePackageCommand(arguments);

            var newPackages = await this.GetInstalledPackages();

            this.UpdatePackageLists(id, source, currentPackages, newPackages);
        }

        /// <summary>
        /// Executes a PowerShell command and returns whether or not there was a result. Optionally calls <see cref="GetInstalledPackages"/>.
        /// </summary>
        /// <param name="commandArgs">The chocolatey command arguments.</param>
        /// <param name="refreshPackages">Whether to force <see cref="GetInstalledPackages"/>.</param>
        /// <returns>Whether or not a result was returned from <see cref="RunDirectChocolateyCommand"/>.</returns>
        public async Task<bool> ExecutePackageCommand(Dictionary<string, object> commandArgs, bool refreshPackages = true)
        {
            try
            {
                await this.RunDirectChocolateyCommand(commandArgs, refreshPackages);
                return true;
            }
            catch (Exception ex)
            {
                this.LogService.Error("ExecutePackageCommmand threw an exception.", ex);
                return false;
            }
        }

        /// <summary>
        /// Executes a PowerShell Command by directly calling chocolatey.ps1. 
        /// </summary>
        /// <param name="commandArgs">
        /// The Chocolatey command arguments.
        /// </param>
        /// <param name="refreshPackages">
        /// Whether to force <see cref="GetInstalledPackages"/>.
        /// </param>
        /// <param name="logOutput">
        /// Whether the output should be logged to the faux PowerShell console or returned as results.
        /// </param>
        /// <returns>
        /// A collection of the output of the PowerShell runspace. Will be empty if <paramref cref="logOutput"/> is true.
        /// </returns>
        public async Task RunDirectChocolateyCommand(Dictionary<string, object> commandArgs, bool refreshPackages = true, bool logOutput = true)
        {
            await this.ProgressService.StartLoading("Chocolatey");
            this.ProgressService.WriteMessage("Processing chocolatey command...");

            var pipeline = this._runspace.CreatePipeline();

            var chocoPath = Path.Combine(this.ChocolateyConfigurationProvider.ChocolateyInstall, "chocolateyinstall", "chocolatey.ps1");

            var powerShellCommand = new Command(chocoPath);

            foreach (var commandArg in commandArgs)
            {
                powerShellCommand.Parameters.Add(commandArg.Key, commandArg.Value);
            }

            pipeline.Commands.Add(powerShellCommand);

            try
            {
                await Task.Run(() => pipeline.Invoke());
            }
            catch (Exception e)
            {
                this.ProgressService.WriteMessage(e.ToString(), PowerShellLineType.Error);
                this.ProgressService.StopLoading().ConfigureAwait(false);
                throw;
            }

            if (logOutput)
            {
                this.ProgressService.WriteMessage("Executed successfully.");
            }

            if (refreshPackages)
            {
                await this.GetInstalledPackages(force: true);
            }

            await this.ProgressService.StopLoading();
        }

        /// <summary>
        /// Executes a PowerShell Command by calling Chocolatey through the PowerShell command line. 
        /// </summary>
        /// <param name="command">
        /// The Chocolatey command arguments.
        /// </param>
        /// <param name="refreshPackages">
        /// Whether to force <see cref="GetInstalledPackages"/>.
        /// </param>
        /// <param name="logOutput">
        /// Whether the output should be logged to the faux PowerShell console or returned as results.
        /// </param>
        /// <returns>
        /// A collection of the output of the PowerShell runspace. Will be empty if <paramref cref="logOutput"/> is true.
        /// </returns>
        public async Task<Collection<PSObject>> RunIndirectChocolateyCommand(string command, bool refreshPackages = true, bool logOutput = true)
        {
            await this.ProgressService.StartLoading("Chocolatey");
            this.ProgressService.WriteMessage("Processing chocolatey command...");

            var pipeline = this._runspace.CreatePipeline();

            pipeline.Commands.AddScript("chocolatey " + command);
            Collection<PSObject> results;

            try
            {
                results = await Task.Run(() => pipeline.Invoke());
            }
            catch (Exception e)
            {
                this.ProgressService.WriteMessage(e.ToString(), PowerShellLineType.Error);
                this.ProgressService.StopLoading().ConfigureAwait(false);
                throw;
            }

            if (logOutput)
            {
                this.ProgressService.WriteMessage("Executed successfully.");
            }

            if (refreshPackages)
            {
                await this.GetInstalledPackages(force: true);
            }

            await this.ProgressService.StopLoading();
            return results;
        }
    }
}