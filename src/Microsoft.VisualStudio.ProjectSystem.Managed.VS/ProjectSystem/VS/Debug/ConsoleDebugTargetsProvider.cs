﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using Microsoft.VisualStudio.IO;
using Microsoft.VisualStudio.ProjectSystem.Debug;
using Microsoft.VisualStudio.ProjectSystem.Properties;
using Microsoft.VisualStudio.ProjectSystem.Utilities;

using Task = System.Threading.Tasks.Task;

namespace Microsoft.VisualStudio.ProjectSystem.VS.Debug
{
    /// <summary>
    /// Provides QueryDebugTargetsAsync() support for running the project output or any random executable. It is not an exported
    /// CPS debugger but hooks into the launch profiles extensibility point. The order of this provider is
    /// near the bottom to ensure other providers get chance to handle it first
    /// </summary>
    [Export(typeof(IDebugProfileLaunchTargetsProvider))]
    [AppliesTo(ProjectCapability.LaunchProfiles)]
    [Order(Order.Default)] // The higher the number the higher priority and we want this one last
    internal class ConsoleDebugTargetsProvider : IDebugProfileLaunchTargetsProvider, IDebugProfileLaunchTargetsProvider2
    {
        private static readonly char[] s_escapedChars = new[] { '^', '<', '>', '&' };

        [ImportingConstructor]
        public ConsoleDebugTargetsProvider(ConfiguredProject configuredProject,
                                           IDebugTokenReplacer tokenReplacer,
                                           IFileSystem fileSystem,
                                           IEnvironmentHelper environment,
                                           IActiveDebugFrameworkServices activeDebugFramework,
                                           ProjectProperties properties)
        {
            TokenReplacer = tokenReplacer;
            TheFileSystem = fileSystem;
            TheEnvironment = environment;
            ActiveDebugFramework = activeDebugFramework;
            Properties = properties;
            Project = configuredProject.UnconfiguredProject;
        }

        private IDebugTokenReplacer TokenReplacer { get; }
        private IFileSystem TheFileSystem { get; }
        private IEnvironmentHelper TheEnvironment { get; }
        private IActiveDebugFrameworkServices ActiveDebugFramework { get; }
        private ProjectProperties Properties { get; }
        private UnconfiguredProject Project { get; }

        private Task<ConfiguredProject> GetConfiguredProjectForDebugAsync()
        {
            return ActiveDebugFramework.GetConfiguredProjectForActiveFrameworkAsync();
        }

        /// <summary>
        /// This provider handles running the Project and empty commandName (this generally just runs the executable)
        /// </summary>
        public bool SupportsProfile(ILaunchProfile profile)
        {
            return string.IsNullOrWhiteSpace(profile.CommandName) || IsRunProjectCommand(profile) || IsRunExecutableCommand(profile);
        }

        /// <summary>
        /// Called just prior to launch to do additional work (put up ui, do special configuration etc).
        /// </summary>
        public Task OnBeforeLaunchAsync(DebugLaunchOptions launchOptions, ILaunchProfile profile)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Called just after the launch to do additional work (put up ui, do special configuration etc).
        /// </summary>
        public Task OnAfterLaunchAsync(DebugLaunchOptions launchOptions, ILaunchProfile profile)
        {
            return Task.CompletedTask;
        }

        private Task<bool> IsClassLibraryAsync()
        {
            return IsOutputTypeAsync(ConfigurationGeneral.OutputTypeValues.Library);
        }

        private Task<bool> IsConsoleAppAsync()
        {
            return IsOutputTypeAsync(ConfigurationGeneral.OutputTypeValues.Exe);
        }

        private async Task<bool> IsOutputTypeAsync(string outputType)
        {
            // Used by default Windows debugger to figure out whether to add an extra
            // pause to end of window when CTRL+F5'ing a console application
            ConfigurationGeneral configuration = await Properties.GetConfigurationGeneralPropertiesAsync()
                                                                 .ConfigureAwait(false);


            var actualOutputType = (IEnumValue)await configuration.OutputType.GetValueAsync()
                                                                             .ConfigureAwait(false);

            return StringComparers.PropertyValues.Equals(actualOutputType.Name, outputType);
        }

        public Task<IReadOnlyList<IDebugLaunchSettings>> QueryDebugTargetsForDebugLaunchAsync(DebugLaunchOptions launchOptions, ILaunchProfile activeProfile)
        {
            return QueryDebugTargetsAsync(launchOptions, activeProfile, validateSettings: true);
        }

        /// <summary>
        /// This is called on F5/Ctrl-F5 to return the list of debug targets.What we return depends on the type
        /// of project.
        /// </summary>
        public Task<IReadOnlyList<IDebugLaunchSettings>> QueryDebugTargetsAsync(DebugLaunchOptions launchOptions, ILaunchProfile activeProfile)
        {
            return QueryDebugTargetsAsync(launchOptions, activeProfile, validateSettings: false);
        }

        private async Task<IReadOnlyList<IDebugLaunchSettings>> QueryDebugTargetsAsync(DebugLaunchOptions launchOptions, ILaunchProfile activeProfile, bool validateSettings)
        {
            var launchSettings = new List<DebugLaunchSettings>();

            // Resolve the tokens in the profile
            ILaunchProfile resolvedProfile = await TokenReplacer.ReplaceTokensInProfileAsync(activeProfile).ConfigureAwait(false);

            // For "run project", we want to launch the process if it's a console app via the command shell when 
            // not debugging, except when this debug session is being launched for profiling.
            bool useCmdShell =
                    IsRunProjectCommand(resolvedProfile) &&
                    (launchOptions & (DebugLaunchOptions.NoDebug | DebugLaunchOptions.Profiling)) == DebugLaunchOptions.NoDebug &&
                    await IsConsoleAppAsync().ConfigureAwait(false);

            DebugLaunchSettings consoleTarget = await GetConsoleTargetForProfile(resolvedProfile, launchOptions, useCmdShell, validateSettings).ConfigureAwait(false);

            launchSettings.Add(consoleTarget);

            return launchSettings.ToArray();
        }

        /// <summary>
        /// Does some basic validation of the settings. If we don't, the error messages are terrible.
        /// </summary>
        public void ValidateSettings(string executable, string workingDir, string profileName)
        {
            if (string.IsNullOrEmpty(executable))
            {
                throw new Exception(string.Format(VSResources.NoDebugExecutableSpecified, profileName));
            }
            else if (executable.IndexOf(Path.DirectorySeparatorChar) != -1 && !TheFileSystem.FileExists(executable))
            {
                throw new Exception(string.Format(VSResources.DebugExecutableNotFound, executable, profileName));
            }
            else if (!string.IsNullOrEmpty(workingDir) && !TheFileSystem.DirectoryExists(workingDir))
            {
                throw new Exception(string.Format(VSResources.WorkingDirecotryInvalid, workingDir, profileName));
            }
        }

        /// <summary>
        /// Helper returns cmd.exe as the launcher for Ctrl-F5 (useCmdShell == true), otherwise just the exe and args passed in.
        /// </summary>
        public static void GetExeAndArguments(bool useCmdShell, string debugExe, string debugArgs, out string finalExePath, out string finalArguments)
        {
            if (useCmdShell)
            {
                // Escape the characters ^<>& so that they are passed to the application rather than interpreted by cmd.exe.
                string escapedArgs = EscapeString(debugArgs, s_escapedChars);
                finalArguments = $"/c \"\"{debugExe}\" {escapedArgs} & pause\"";
                finalExePath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            }
            else
            {
                finalArguments = debugArgs;
                finalExePath = debugExe;
            }
        }

        private static bool IsRunExecutableCommand(ILaunchProfile profile)
        {
            return string.Equals(profile.CommandName, LaunchSettingsProvider.RunExecutableCommandName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRunProjectCommand(ILaunchProfile profile)
        {
            return string.Equals(profile.CommandName, LaunchSettingsProvider.RunProjectCommandName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// This is called on F5 to return the list of debug targets. What we return depends on the type
        /// of project.
        /// </summary>
        private async Task<DebugLaunchSettings> GetConsoleTargetForProfile(ILaunchProfile resolvedProfile, DebugLaunchOptions launchOptions,
                                                                           bool useCmdShell, bool validateSettings)
        {
            var settings = new DebugLaunchSettings(launchOptions);

            string executable, arguments;

            string projectFolder = Path.GetDirectoryName(Project.FullPath);
            ConfiguredProject configuredProject = await GetConfiguredProjectForDebugAsync().ConfigureAwait(false);

            // If no working directory specified in the profile, we default to output directory. If for some reason the output directory
            // is not specified, fall back to the project folder.
            string defaultWorkingDir = await GetOutputDirectoryAsync(configuredProject).ConfigureAwait(false);
            if (string.IsNullOrEmpty(defaultWorkingDir))
            {
                defaultWorkingDir = projectFolder;
            }
            else
            {
                if (!Path.IsPathRooted(defaultWorkingDir))
                {
                    defaultWorkingDir = TheFileSystem.GetFullPath(Path.Combine(projectFolder, defaultWorkingDir));
                }

                // If the directory at OutDir doesn't exist, fall back to the project folder
                if (!TheFileSystem.DirectoryExists(defaultWorkingDir))
                {
                    defaultWorkingDir = projectFolder;
                }
            }

            // Is this profile just running the project? If so we ignore the exe
            if (IsRunProjectCommand(resolvedProfile))
            {
                // Get the executable to run, the arguments and the default working directory
                Tuple<string, string, string> runData = await GetRunnableProjectInformationAsync(configuredProject, validateSettings).ConfigureAwait(false);
                executable = runData.Item1;
                arguments = runData.Item2;
                if (!string.IsNullOrWhiteSpace(runData.Item3))
                {
                    defaultWorkingDir = runData.Item3;
                }

                if (!string.IsNullOrWhiteSpace(resolvedProfile.CommandLineArgs))
                {
                    arguments = arguments + " " + resolvedProfile.CommandLineArgs;
                }
            }
            else
            {
                executable = resolvedProfile.ExecutablePath;
                arguments = resolvedProfile.CommandLineArgs;
            }

            string workingDir;
            if (string.IsNullOrWhiteSpace(resolvedProfile.WorkingDirectory))
            {
                workingDir = defaultWorkingDir;
            }
            else
            {
                // If the working directory is not rooted we assume it is relative to the project directory
                workingDir = TheFileSystem.GetFullPath(Path.Combine(projectFolder, resolvedProfile.WorkingDirectory.Replace("/", "\\")));
            }

            // IF the executable is not rooted, we want to make is relative to the workingDir unless is doesn't contain
            // any path elements. In that case we are going to assume it is in the current directory of the VS process, or on
            // the environment path. If we can't find it, we just launch it as before.
            if (!string.IsNullOrWhiteSpace(executable))
            {
                executable = executable.Replace("/", "\\");
                if (Path.GetPathRoot(executable) == "\\")
                {
                    // Root of current drive
                    executable = TheFileSystem.GetFullPath(executable);
                }
                else if (!Path.IsPathRooted(executable))
                {
                    if (executable.Contains("\\"))
                    {
                        // Combine with the working directory used by the profile
                        executable = TheFileSystem.GetFullPath(Path.Combine(workingDir, executable));
                    }
                    else
                    {
                        // Try to resolve against the current working directory (for compat) and failing that, the environment path.
                        string exeName = executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? executable : executable + ".exe";
                        string fullPath = TheFileSystem.GetFullPath(exeName);
                        if (TheFileSystem.FileExists(fullPath))
                        {
                            executable = fullPath;
                        }
                        else
                        {
                            fullPath = GetFullPathOfExeFromEnvironmentPath(exeName);
                            if (fullPath != null)
                            {
                                executable = fullPath;
                            }
                        }
                    }
                }
            }

            if (validateSettings)
            {
                ValidateSettings(executable, workingDir, resolvedProfile.Name);
            }

            GetExeAndArguments(useCmdShell, executable, arguments, out string finalExecutable, out string finalArguments);

            // Apply environment variables.
            if (resolvedProfile.EnvironmentVariables != null && resolvedProfile.EnvironmentVariables.Count > 0)
            {
                foreach (KeyValuePair<string, string> kvp in resolvedProfile.EnvironmentVariables)
                {
                    settings.Environment[kvp.Key] = kvp.Value;
                }
            }

            settings.Executable = finalExecutable;
            settings.Arguments = finalArguments;
            settings.CurrentDirectory = workingDir;
            settings.LaunchOperation = DebugLaunchOperation.CreateProcess;
            settings.LaunchDebugEngineGuid = await GetDebuggingEngineAsync(configuredProject).ConfigureAwait(false);

            if (resolvedProfile.NativeDebuggingIsEnabled())
            {
                settings.AdditionalDebugEngines.Add(DebuggerEngines.NativeOnlyEngine);
            }

            if (settings.Environment.Count > 0)
            {
                settings.LaunchOptions = settings.LaunchOptions | DebugLaunchOptions.MergeEnvironment;
            }

            return settings;
        }

        /// <summary>
        /// Queries properties from the project to get information on how to run the application. The returned Tuple contains:
        /// exeToRun, arguments, workingDir
        /// </summary>
        private async Task<Tuple<string, string, string>> GetRunnableProjectInformationAsync(
            ConfiguredProject configuredProject,
            bool validateSettings)
        {
            IProjectProperties properties = configuredProject.Services.ProjectPropertiesProvider.GetCommonProperties();

            string runCommand = await GetTargetCommandAsync(properties, validateSettings).ConfigureAwait(false);
            string runWorkingDirectory = await properties.GetEvaluatedPropertyValueAsync("RunWorkingDirectory").ConfigureAwait(false);
            string runArguments = await properties.GetEvaluatedPropertyValueAsync("RunArguments").ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(runCommand))
            {
                throw new Exception(VSResources.NoRunCommandSpecifiedInProject);
            }

            // If the working directory is relative, it will be relative to the project root so make it a full path
            if (!string.IsNullOrWhiteSpace(runWorkingDirectory) && !Path.IsPathRooted(runWorkingDirectory))
            {
                runWorkingDirectory = Path.Combine(Path.GetDirectoryName(Project.FullPath), runWorkingDirectory);
            }

            return new Tuple<string, string, string>(runCommand, runArguments, runWorkingDirectory);
        }

        private async Task<string> GetTargetCommandAsync(
            IProjectProperties properties,
            bool validateSettings)
        {
            // First try "RunCommand" property
            string runCommand = await GetRunCommandAsync(properties).ConfigureAwait(false);

            if (string.IsNullOrEmpty(runCommand))
            {
                // If we're launching for debug purposes, prevent someone F5'ing a class library
                if (validateSettings && await IsClassLibraryAsync().ConfigureAwait(false))
                {
                    throw new Exception(VSResources.ProjectNotRunnableDirectly);
                }

                // Otherwise, fall back to "TargetPath"
                runCommand = await properties.GetEvaluatedPropertyValueAsync(ConfigurationGeneral.TargetPathProperty)
                                             .ConfigureAwait(false);
            }

            return runCommand;
        }


        private async Task<string> GetRunCommandAsync(IProjectProperties properties)
        {
            string runCommand = await properties.GetEvaluatedPropertyValueAsync("RunCommand")
                                                .ConfigureAwait(false);

            if (string.IsNullOrEmpty(runCommand))
                return null;

            // If dotnet.exe is used runCommand returns just "dotnet". The debugger is going to require a full path so we need to append the .exe
            // extension.
            if (!runCommand.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                runCommand += ".exe";
            }

            // If the path is just the name of an exe like dotnet.exe then we try to find it on the path
            if (runCommand.IndexOf(Path.DirectorySeparatorChar) == -1)
            {
                string executable = GetFullPathOfExeFromEnvironmentPath(runCommand);
                if (executable != null)
                {
                    runCommand = executable;
                }
            }

            return runCommand;
        }

        private static async Task<string> GetOutputDirectoryAsync(ConfiguredProject configuredProject)
        {
            IProjectProperties properties = configuredProject.Services.ProjectPropertiesProvider.GetCommonProperties();
            string outdir = await properties.GetEvaluatedPropertyValueAsync("OutDir").ConfigureAwait(false);

            return outdir;
        }
        private static async Task<Guid> GetDebuggingEngineAsync(ConfiguredProject configuredProject)
        {
            IProjectProperties properties = configuredProject.Services.ProjectPropertiesProvider.GetCommonProperties();
            string framework = await properties.GetEvaluatedPropertyValueAsync("TargetFrameworkIdentifier").ConfigureAwait(false);

            return ProjectDebuggerProvider.GetManagedDebugEngineForFramework(framework);
        }

        /// <summary>
        /// Searchs the path variable for the first match of exeToSearchFor. Returns
        /// null if not found.
        /// </summary>
        public string GetFullPathOfExeFromEnvironmentPath(string exeToSearchFor)
        {
            string pathEnv = TheEnvironment.GetEnvironmentVariable("Path");

            if (string.IsNullOrEmpty(pathEnv))
            {
                return null;
            }

            string[] paths = pathEnv.Split(Delimiter.Semicolon, StringSplitOptions.RemoveEmptyEntries);
            foreach (string path in paths)
            {
                // We don't want one bad path entry to derail the search
                try
                {
                    string exePath = Path.Combine(path, exeToSearchFor);
                    if (TheFileSystem.FileExists(exePath))
                    {
                        return exePath;
                    }
                }
                catch (ArgumentException)
                {
                }
            }

            return null;
        }

        /// <summary>
        /// Escapes the given characters in a given string, ignoring escape sequences when inside a quoted string.
        /// </summary>
        /// <param name="unescaped">The string to escape.</param>
        /// <param name="toEscape">The characters to escape in the string.</param>
        /// <returns>The escaped string.</returns>
        internal static string EscapeString(string unescaped, char[] toEscape)
        {
            if (string.IsNullOrWhiteSpace(unescaped))
                return unescaped;

            bool ShouldEscape(char c)
            {
                foreach (char escapeChar in toEscape)
                {
                    if (escapeChar == c)
                        return true;
                }
                return false;
            }

            StringState currentState = StringState.NormalCharacter;
            var finalBuilder = new StringBuilder();
            foreach (char currentChar in unescaped)
            {
                switch (currentState)
                {
                    case StringState.NormalCharacter:
                        // If we're currently not in a quoted string, then we need to escape anything in toEscape.
                        // The valid transitions are to EscapedCharacter (for a '\', such as '\"'), and QuotedString.
                        if (currentChar == '\\')
                        {
                            currentState = StringState.EscapedCharacter;
                        }
                        else if (currentChar == '"')
                        {
                            currentState = StringState.QuotedString;
                        }
                        else if (ShouldEscape(currentChar))
                        {
                            finalBuilder.Append('^');
                        }

                        finalBuilder.Append(currentChar);
                        break;
                    case StringState.EscapedCharacter:
                        // If a '\' was the previous character, then we blindly append to the string, escaping if necessary,
                        // and move back to NormalCharacter. This handles '\"'
                        if (ShouldEscape(currentChar))
                        {
                            finalBuilder.Append('^');
                        }

                        finalBuilder.Append(currentChar);
                        currentState = StringState.NormalCharacter;
                        break;
                    case StringState.QuotedString:
                        // If we're in a string, we don't escape any characters. If the current character is a '\',
                        // then we move to QuotedStringEscapedCharacter. This handles '\"'. If the current character
                        // is a '"', then we're out of the string. Otherwise, we stay in the string.
                        if (currentChar == '\\')
                        {
                            currentState = StringState.QuotedStringEscapedCharacter;
                        }
                        else if (currentChar == '"')
                        {
                            currentState = StringState.NormalCharacter;
                        }

                        finalBuilder.Append(currentChar);
                        break;
                    case StringState.QuotedStringEscapedCharacter:
                        // If we have one slash, then we blindly append to the string, no escaping, and move back to
                        // QuotedString. This handles escaped '"' inside strings.
                        finalBuilder.Append(currentChar);
                        currentState = StringState.QuotedString;
                        break;
                    default:
                        // We can't get here.
                        throw new InvalidOperationException();
                }
            }

            return finalBuilder.ToString();
        }

        private enum StringState
        {
            NormalCharacter, EscapedCharacter, QuotedString, QuotedStringEscapedCharacter
        }
    }
}
