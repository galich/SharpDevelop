﻿// Copyright (c) AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under the GNU LGPL (for details please see \doc\license.txt)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Effects;
using ICSharpCode.Core;
using Microsoft.Build.Exceptions;

namespace ICSharpCode.SharpDevelop.Project
{
	sealed class SolutionLoader : IDisposable
	{
		readonly FileName fileName;
		readonly TextReader textReader;
		string currentLine;
		int lineNumber;
		
		public SolutionLoader(TextReader textReader)
		{
			this.textReader = textReader;
			NextLine();
		}
		
		public SolutionLoader(FileName fileName)
		{
			this.fileName = fileName;
			// read solution files using system encoding, but detect UTF8 if BOM is present
			this.textReader = new StreamReader(fileName, Encoding.Default, true);
			NextLine();
		}
		
		public void Dispose()
		{
			textReader.Dispose();
		}
		
		void NextLine()
		{
			do {
				currentLine = textReader.ReadLine();
				lineNumber++;
			} while (currentLine != null && (currentLine.Length == 0 || currentLine[0] == '#'));
		}
		
		InvalidProjectFileException Error()
		{
			return Error("${res:SharpDevelop.Solution.InvalidSolutionFile}");
		}
		
		InvalidProjectFileException Error(string message, params object[] formatItems)
		{
			if (formatItems.Length > 0)
				message = StringParser.Format(message, formatItems);
			else
				message = StringParser.Parse(message);
			return new InvalidProjectFileException(fileName ?? string.Empty, lineNumber, 1, lineNumber, currentLine.Length + 1, message, string.Empty, string.Empty, string.Empty);
		}
		
		#region ReadSolution
		public void ReadSolution(Solution solution, IProgressMonitor progress)
		{
			ReadFormatHeader();
			
			// Read solution folder and project entries:
			var solutionEntries = new List<ProjectLoadInformation>();
			var projectInfoDict = new Dictionary<Guid, ProjectLoadInformation>();
			var solutionFolderDict = new Dictionary<Guid, SolutionFolder>();
			int projectCount = 0;
			bool fixedGuidConflicts = false;
			
			ProjectLoadInformation information;
			while ((information = ReadProjectEntry(solution)) != null) {
				solutionEntries.Add(information);
				if (projectInfoDict.ContainsKey(information.IdGuid)) {
					// resolve GUID conflicts
					information.IdGuid = Guid.NewGuid();
					fixedGuidConflicts = true;
				}
				projectInfoDict.Add(information.IdGuid, information);
				
				if (information.TypeGuid == ProjectTypeGuids.SolutionFolder) {
					solutionFolderDict.Add(information.IdGuid, CreateSolutionFolder(solution, information));
				} else {
					projectCount++;
				}
			}
			
			progress.CancellationToken.ThrowIfCancellationRequested();
			
			// Read global sections:
			if (currentLine != "Global")
				throw Error();
			NextLine();
			
			Dictionary<Guid, SolutionFolder> guidToParentFolderDict = null;
			
			SolutionSection section;
			while ((section = ReadSection(isGlobal: true)) != null) {
				switch (section.SectionName) {
					case "SolutionConfigurationPlatforms":
						var configurations = LoadSolutionConfigurations(section);
						foreach (var config in configurations.Select(c => c.Configuration).Distinct(ConfigurationAndPlatform.ConfigurationNameComparer))
							solution.ConfigurationNames.Add(config, null);
						foreach (var platform in configurations.Select(c => c.Platform).Distinct(ConfigurationAndPlatform.ConfigurationNameComparer))
							solution.PlatformNames.Add(platform, null);
						break;
					case "ProjectConfigurationPlatforms":
						LoadProjectConfigurations(section, projectInfoDict);
						break;
					case "NestedProjects":
						guidToParentFolderDict = LoadNesting(section, solutionFolderDict);
						break;
					default:
						solution.GlobalSections.Add(section);
						break;
				}
			}
			if (currentLine != "EndGlobal")
				throw Error();
			NextLine();
			if (currentLine != null)
				throw Error();
			
			solution.LoadPreferences();
			
			// Now that the project configurations have been set, we can actually load the projects:
			int projectsLoaded = 0;
			foreach (var projectInfo in solutionEntries) {
				ISolutionItem solutionItem;
				if (projectInfo.TypeGuid == ProjectTypeGuids.SolutionFolder) {
					solutionItem = solutionFolderDict[projectInfo.IdGuid];
				} else {
					// Load project:
					projectInfo.ProjectConfiguration = projectInfo.ConfigurationMapping.GetProjectConfiguration(solution.ActiveConfiguration);
					progress.TaskName = "Loading " + projectInfo.ProjectName;
					using (projectInfo.ProgressMonitor = progress.CreateSubTask(1.0 / projectCount)) {
						solutionItem = ProjectBindingService.LoadProject(projectInfo);
					}
					projectsLoaded++;
					progress.Progress = (double)projectsLoaded / projectCount;
				}
				// Add solutionItem to solution:
				SolutionFolder folder;
				if (guidToParentFolderDict != null && guidToParentFolderDict.TryGetValue(projectInfo.IdGuid, out folder)) {
					folder.Items.Add(solutionItem);
				} else {
					solution.Items.Add(solutionItem);
				}
			}
			
			solution.IsDirty = fixedGuidConflicts; // reset IsDirty=false unless we've fixed GUID conflicts
		}
		#endregion
		
		#region ReadFormatHeader
		static Regex versionPattern = new Regex(@"^Microsoft Visual Studio Solution File, Format Version\s+(?<Version>[\d\.]+)\s*$");
		
		public SolutionFormatVersion ReadFormatHeader()
		{
			Match match = versionPattern.Match(currentLine);
			if (!match.Success)
				throw Error();
			
			SolutionFormatVersion version;
			switch (match.Result("${Version}")) {
				case "7.00":
				case "8.00":
					throw Error("${res:SharpDevelop.Solution.CannotLoadOldSolution}");
				case "9.00":
					version = SolutionFormatVersion.VS2005;
					break;
				case "10.00":
					version = SolutionFormatVersion.VS2008;
					break;
				case "11.00":
					version = SolutionFormatVersion.VS2010;
					break;
				case "12.00":
					version = SolutionFormatVersion.VS2012;
					break;
				default:
					throw Error("${res:SharpDevelop.Solution.UnknownSolutionVersion}", match.Result("${Version}"));
			}
			NextLine();
			return version;
		}
		#endregion
		
		#region ReadSection
		void ReadSectionEntries(SolutionSection section)
		{
			while (currentLine != null) {
				int pos = currentLine.IndexOf('=');
				if (pos < 0)
					break; // end of section
				string key = currentLine.Substring(0, pos).Trim();
				string value = currentLine.Substring(pos + 1).Trim();
				section.Add(key, value);
				NextLine();
			}
		}
		
		static readonly Regex sectionHeaderPattern = new Regex("^\\s*(Global|Project)Section\\((?<Name>.*)\\)\\s*=\\s*(?<Type>.*)\\s*$");
		
		public SolutionSection ReadSection(bool isGlobal)
		{
			if (currentLine == null)
				return null;
			Match match = sectionHeaderPattern.Match(currentLine);
			if (!match.Success)
				return null;
			NextLine();
			
			SolutionSection section = new SolutionSection(match.Groups["Name"].Value, match.Groups["Type"].Value);
			ReadSectionEntries(section);
			string expectedLine = isGlobal ? "EndGlobalSection" : "EndProjectSection";
			if ((currentLine ?? string.Empty).Trim() != expectedLine)
				throw Error("Expected " + expectedLine);
			NextLine();
			return section;
		}
		#endregion
		
		#region ReadProjectEntry
		static readonly Regex projectLinePattern = new Regex("^\\s*Project\\(\"(?<TypeGuid>.*)\"\\)\\s+=\\s+\"(?<Title>.*)\",\\s*\"(?<Location>.*)\",\\s*\"(?<IdGuid>.*)\"\\s*$");
		
		public ProjectLoadInformation ReadProjectEntry(Solution parentSolution)
		{
			if (currentLine == null)
				return null;
			Match match = projectLinePattern.Match(currentLine);
			if (!match.Success)
				return null;
			NextLine();
			string title = match.Groups["Title"].Value;
			string location = match.Groups["Location"].Value;
			FileName projectFileName = FileName.Create(Path.Combine(parentSolution.Directory, location));
			var loadInformation = new ProjectLoadInformation(parentSolution, projectFileName, title);
			loadInformation.TypeGuid = Guid.Parse(match.Groups["TypeGuid"].Value);
			loadInformation.IdGuid = Guid.Parse(match.Groups["IdGuid"].Value);
			loadInformation.ConfigurationMapping = new ConfigurationMapping(parentSolution);
			SolutionSection section;
			while ((section = ReadSection(isGlobal: false)) != null) {
				loadInformation.ProjectSections.Add(section);
			}
			if (currentLine != "EndProject")
				throw Error();
			NextLine();
			return loadInformation;
		}
		#endregion
		
		#region Load Configurations
		IEnumerable<ConfigurationAndPlatform> LoadSolutionConfigurations(IEnumerable<KeyValuePair<string, string>> section)
		{
			// Entries in the section look like this: 'Debug|Any CPU = Debug|Any CPU'
			return section.Select(e => ConfigurationAndPlatform.FromKey(e.Key));
		}
		
		void LoadProjectConfigurations(SolutionSection section, Dictionary<Guid, ProjectLoadInformation> projectInfoDict)
		{
			foreach (var pair in section) {
				// pair is an entry like this: '{35CEF10F-2D4C-45F2-9DD1-161E0FEC583C}.Debug|Any CPU.ActiveCfg = Debug|Any CPU'
				if (pair.Key.EndsWith(".ActiveCfg", StringComparison.OrdinalIgnoreCase)) {
					Guid guid;
					ConfigurationAndPlatform solutionConfig;
					if (!TryParseProjectConfigurationKey(pair.Key, out guid, out solutionConfig))
						continue;
					ProjectLoadInformation projectInfo;
					if (!projectInfoDict.TryGetValue(guid, out projectInfo))
						continue;
					var projectConfig = ConfigurationAndPlatform.FromKey(pair.Value);
					if (projectConfig == default(ConfigurationAndPlatform))
						continue;
					projectInfo.ConfigurationMapping.SetProjectConfiguration(solutionConfig, projectConfig);
					// Disable build if we see a '.ActiveCfg' entry.
					projectInfo.ConfigurationMapping.SetBuildEnabled(solutionConfig, false);
				}
			}
			// Re-enable build if we see a '.Build.0' entry:
			foreach (var pair in section) {
				// pair is an entry like this: '{35CEF10F-2D4C-45F2-9DD1-161E0FEC583C}.Debug|Any CPU.Build.0 = Debug|Any CPU'
				if (pair.Key.EndsWith(".Build.0", StringComparison.OrdinalIgnoreCase)) {
					Guid guid;
					ConfigurationAndPlatform solutionConfig;
					if (!TryParseProjectConfigurationKey(pair.Key, out guid, out solutionConfig))
						continue;
					ProjectLoadInformation projectInfo;
					if (!projectInfoDict.TryGetValue(guid, out projectInfo))
						continue;
					projectInfo.ConfigurationMapping.SetBuildEnabled(solutionConfig, true);
				}
			}
		}
		
		bool TryParseProjectConfigurationKey(string key, out Guid guid, out ConfigurationAndPlatform config)
		{
			guid = default(Guid);
			config = default(ConfigurationAndPlatform);
			
			int firstDot = key.IndexOf('.');
			int secondDot = key.IndexOf('.', firstDot + 1);
			if (firstDot < 0 || secondDot < 0)
				return false;
			
			string guidText = key.Substring(0, firstDot);
			if (!Guid.TryParse(guidText, out guid))
				return false;
			
			string configKey = key.Substring(firstDot + 1, secondDot - (firstDot + 1));
			config = ConfigurationAndPlatform.FromKey(configKey);
			return config != default(ConfigurationAndPlatform);
		}
		#endregion
		
		#region Load Nesting
		SolutionFolder CreateSolutionFolder(Solution solution, ProjectLoadInformation information)
		{
			var folder = new SolutionFolder(solution, information.IdGuid);
			folder.Name = information.ProjectName;
			// Add solution items:
			var solutionItemsSection = information.ProjectSections.FirstOrDefault(s => s.SectionName == "SolutionItems");
			if (solutionItemsSection != null) {
				foreach (string location in solutionItemsSection.Values) {
					var fileItem = new SolutionFileItem(solution);
					fileItem.FileName = FileName.Create(Path.Combine(information.Solution.Directory, location));
					folder.Items.Add(fileItem);
				}
			}
			return folder;
		}
		
		/// <summary>
		/// Converts the 'NestedProjects' section into a dictionary from project GUID to parent solution folder.
		/// </summary>
		Dictionary<Guid, SolutionFolder> LoadNesting(SolutionSection section, IReadOnlyDictionary<Guid, SolutionFolder> solutionFolderDict)
		{
			var result = new Dictionary<Guid, SolutionFolder>();
			foreach (var entry in section) {
				Guid idGuid;
				Guid parentGuid;
				if (Guid.TryParse(entry.Key, out idGuid) && Guid.TryParse(entry.Value, out parentGuid)) {
					SolutionFolder parentFolder;
					if (solutionFolderDict.TryGetValue(parentGuid, out parentFolder))
						result[idGuid] = parentFolder;
				}
			}
			return result;
		}
		#endregion
	}
}