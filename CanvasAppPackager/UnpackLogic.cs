﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using CanvasAppPackager.Poco;
using CanvasAppPackager.Args;

namespace CanvasAppPackager
{
    class UnpackLogic
    {
        public const string CodeFileExt = ".js";
        public const string DataFileExt = ".json";
        public const string EndOfRuleCode = "} // End of ";
        public static readonly Version MinimumDocVersion = new Version("1.280");
        public const string UnformattedPrefix = "//// Unformatted: ";

        public struct Paths
        {
            public const string PackageApps = "apps";
            public const string Apps = "Apps";
            public const string AutoValues = "AutoValues";
            public const string BackgroundImage = "BackgroundImage.png";
            public const string Code = "Code";
            public const string ComponentCode = "ComponentCode";
            public const string Components = "Components";
            public const string Controls = "Controls";
            public const string Header = "Header.json";
            public const string Icons = "Icons";
            public const string ManifestFileName = "manifest.json";
            public const string Metadata = "MetadataFiles";
            public const string MsPowerApps = "Microsoft.PowerApps";
            public const string Resources = "Resources";
            public const string ResourcePublishFileName = "PublishInfo.json";
            public const string LogoImage = "Logo";
        }

        public static void Unpack(string file, string outputDirectory, Args.Args options)
        {
            if (options.Clobber && Directory.Exists(outputDirectory))
            {
                Logger.Log("Deleting files in " + outputDirectory);
                Directory.Delete(outputDirectory, true);
            }

            Logger.Log("Extracting files from " + file + " to " + outputDirectory);
            if (Path.GetExtension(file).ToLower() == ".zip")
            {
                ZipFile.ExtractToDirectory(file, outputDirectory, true);
                ExtractApps(outputDirectory, options);
            }
            else
            {
                MsAppHelper.ExtractToDirectory(file, outputDirectory, true);
                if (!options.OnlyExtract)
                {
                    ExtractCanvasApp(outputDirectory, options);
                }
            }
        }

        private static void ExtractApps(string outputDirectory, Args.Args options)
        {
            if (!Directory.Exists(Path.Combine(outputDirectory, Paths.MsPowerApps)))
            {
                Logger.Error($"Invalid zip file.  Missing root folder \"{Paths.MsPowerApps}\"");
                return;
            }

            var appsPath = Path.Combine(outputDirectory, Paths.MsPowerApps, Paths.PackageApps);
            foreach (var appSourcePath in Directory.GetDirectories(appsPath))
            {
                var appInfo = AppInfo.Parse(File.ReadAllText(Path.Join(appSourcePath, Path.GetFileName(appSourcePath)) + ".json"));
                var appOutputPath = Path.Combine(outputDirectory, Paths.Apps, string.IsNullOrWhiteSpace(options.NameOfApplication) ? appInfo.DisplayName : options.NameOfApplication);
                Logger.Log($"Extracting App {appInfo.DisplayName} - {appInfo.Description}");
                var msAppFilePath = Path.Combine(appsPath, appInfo.MsAppPath);
                Unpack(msAppFilePath, appOutputPath, options);
                MoveMetadataFiles(appInfo, appOutputPath, msAppFilePath);
            }
        }

        private static void MoveMetadataFiles(AppInfo appInfo, string appOutputPath, string msAppFilePath)
        {
            var metadataFilesPath = Path.Combine(appOutputPath, Paths.Metadata);
            Directory.CreateDirectory(metadataFilesPath);
            Logger.Log($"Copying metadata files from {Path.GetDirectoryName(msAppFilePath)} to {metadataFilesPath}");
            File.Delete(msAppFilePath);
            var fileMapping = GetMetadataFileMappings(appInfo);
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(msAppFilePath)))
            {
                var destinationFile = Path.Combine(metadataFilesPath, Path.GetFileName(file));
                var appInfoFileName = Path.GetRelativePath(Directory.GetParent(msAppFilePath)?.Parent?.FullName, file);
                if (fileMapping.TryGetValue(appInfoFileName, out var mappedTo))
                {
                    destinationFile = Path.Combine(metadataFilesPath, mappedTo);
                }
                if (File.Exists(destinationFile))
                {
                    File.Delete(destinationFile);
                }

                if (!Directory.Exists(Path.GetDirectoryName(destinationFile)))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationFile));
                }
                File.Move(file, destinationFile);

                FormatJsonFile(destinationFile);
            }
        }

        private static void FormatJsonFile(string destinationFile)
        {
            if (Path.GetExtension(destinationFile)?.ToLower() != ".json")
            {
                return;
            }

            var fileLines = File.ReadAllLines(destinationFile);
            if (fileLines.Length == 1)
            {
                File.WriteAllText(destinationFile, UnformattedPrefix
                                                   + fileLines[0]
                                                   + Environment.NewLine
                                                   + JsonConvert.SerializeObject(JsonConvert.DeserializeObject(fileLines[0]), Formatting.Indented));
            }
        }

        private static void RenameAutoNamedFiles(string appDirectory)
        {
            var resourceFilesPath = Path.Combine(appDirectory, Paths.Resources);
            var publishInfo = Path.Combine(resourceFilesPath, Paths.ResourcePublishFileName);
            Logger.Log("Extracting file " + publishInfo);
            var json = File.ReadAllText(publishInfo);
            var info = JsonConvert.DeserializeObject<PublishInfo>(json);

            if (!string.IsNullOrEmpty(info.LogoFileName)) // Logo file is optional 
            {
                var fromName = Path.Combine(resourceFilesPath, info.LogoFileName);
                var toName = Path.Combine(resourceFilesPath, Paths.LogoImage + Path.GetExtension(info.LogoFileName));
                Logger.Log($"Renaming auto named file '{fromName}' to '{toName}'.");
                File.Delete(toName);
                File.Move(fromName, toName);
            }
        }

        private static Dictionary<string, string> GetMetadataFileMappings(AppInfo appInfo)
        {
            var fileMapping = new Dictionary<string, string> { { appInfo.BackgroundImage, Paths.BackgroundImage } };
            foreach (var icon in appInfo.Icons)
            {
                var key = icon.Key;
                if (key.EndsWith("Uri"))
                {
                    key = key.Substring(0, key.Length - 3) + ".png";
                }

                fileMapping.Add(icon.Value, Path.Join(Paths.Icons, key));
            }

            return fileMapping;
        }

        private static void ExtractCanvasApp(string appDirectory, Args.Args options)
        {
            var codeDirectory = Path.Combine(appDirectory, Paths.Code);
            var componentCodeDirectory = Path.Combine(appDirectory, Paths.ComponentCode);
            var controlsDir = Path.Combine(appDirectory, Paths.Controls);
            var componentsDir = Path.Combine(appDirectory, Paths.Components);
            var header = File.ReadAllText(Path.Combine(appDirectory, Paths.Header));


            // It's important to use proper JSON deserialization since the whitepsace is unpredictable. 
            var headerObj = JsonConvert.DeserializeObject<Header>(header);                        
            var version    = new Version(headerObj.DocVersion);

            ExtraCodeful(controlsDir, codeDirectory, version, options);
            ExtraCodeful(componentsDir, componentCodeDirectory, version, options);

            RenameAutoNamedFiles(appDirectory);
        }

        private static void ExtraCodeful(string controlsDir, string codeDirectory, Version version, Args.Args options)
        {
            if (!Directory.Exists(controlsDir))
            {
                return;
            }
            var autoValueExtractor = new AutoValueExtractor();

            foreach (var file in Directory.GetFiles(controlsDir))
            {
                Logger.Log("Extracting file " + file);
                var json = File.ReadAllText(file);
                if (!string.IsNullOrWhiteSpace(options.RenameCopiedControlOldPostfix))
                {
                    json = RenameControls(json, options);
                }
                var screen = JsonConvert.DeserializeObject<CanvasAppScreen>(json);
                VerifySerialization(screen, json, file, version);
                var fileDirectory = Path.Combine(codeDirectory, screen.TopParent.Name);
                ParseControl(screen, screen.TopParent, fileDirectory, autoValueExtractor);
            }
            File.WriteAllText(Path.Combine(codeDirectory, Paths.AutoValues) + DataFileExt, autoValueExtractor.Serialize());
            Directory.Delete(controlsDir, true);
         }

        private static string RenameControls(string app, Args.Args options)
        {
            Logger.Log($"Renaming Controls from \"{options.RenameCopiedControlOldPostfix}\" to \"{options.RenameCopiedControlNewPostfix}\".");
            return app.Replace(options.RenameCopiedControlOldPostfix, options.RenameCopiedControlNewPostfix);
        }

        private static void VerifySerialization(CanvasAppScreen screen, string json, string file, Version docVersion)
        {
            var isJsonFormatted = json.Length > 2 && json[1] == '\r' && json[2] == '\n';
            var newJson = screen.Serialize(isJsonFormatted
                                               ? Formatting.Indented
                                               : Formatting.None);
            newJson = FixSerializationExceptions(newJson);
            if (json != newJson)
            {
                if (docVersion < MinimumDocVersion)
                {
                    throw new Exception($"The version of the canvas app is too old!  App version {docVersion}.  Minimum Version {MinimumDocVersion}");
                }
                var jsonFile = Path.Combine(Path.GetDirectoryName(file), Path.GetFileName(file)) + ".original";
                // ReSharper disable once StringLiteralTypo
                var newJsonFile = Path.Combine(Path.GetDirectoryName(jsonFile), Path.GetFileNameWithoutExtension(jsonFile)) + ".reserialized";
                WriteJsonFileWithFormattedCopy(jsonFile, json);
                WriteJsonFileWithFormattedCopy(newJsonFile, newJson);
                var errorMsg = CreateDiffErrorMsg(json, file, newJson, jsonFile, newJsonFile, false);
                jsonFile += ".json";
                newJsonFile += ".json";
                errorMsg += CreateDiffErrorMsg(File.ReadAllText(jsonFile), file, File.ReadAllText(newJsonFile), jsonFile, newJsonFile, true);
                throw new
                    Exception("Unable to re-serialize json to match source!  " + errorMsg);



            }
        }

        private static string CreateDiffErrorMsg(string json, string file, string newJson, string jsonFile, string newJsonFile, bool formatted)
        {
            var shortest = json.Length > newJson.Length
                ? newJson
                : json;
            var errorScope = Environment.NewLine + Environment.NewLine;
            var lineNumber = 0;
            var linePosition = 0;

            var firstDifferentChar = shortest.Length;
            for (var i = 0; i < shortest.Length; i++)
            {
                if (json[i] == '\n')
                {
                    lineNumber++;
                    linePosition = 0;
                }
                else
                {
                    linePosition++;
                }

                if (json[i] == newJson[i])
                {
                    continue;
                }

                if (lineNumber == 0 && formatted)
                {
                    // Skip the Unformatted Line
                    continue;
                }
                firstDifferentChar = i;
                break;
            }

            var formatText = formatted ? " formatted " : " ";
            return $"Character at position: {firstDifferentChar} on line: {lineNumber} at {linePosition} is not correct.  To prevent potential app defects, extracting file {file} has stopped.{Environment.NewLine}See '{jsonFile}' for extracted{formatText}version vs output{formatText}version '{newJsonFile}'.{(string.IsNullOrWhiteSpace(errorScope) ? string.Empty : errorScope)}";
        }

        private static void WriteJsonFileWithFormattedCopy(string jsonFile, string json)
        {
            File.WriteAllText(jsonFile, json);
            File.Copy(jsonFile, jsonFile + ".json", true);
            FormatJsonFile(jsonFile + ".json");
        }

        private static string FixSerializationExceptions(string newJson)
        {
            if (newJson.Contains("\"DynamicControlDefinitionJson\": "))
            {
                // Contains PCF Control fix issue with TemplateDisplayName being populated with a null value
                var lines = new List<string>(newJson.Split(Environment.NewLine));
                for (var i = 0; i < lines.Count; i++)
                {
                    if (lines[i].Length > 0 
                        && lines[i].TrimStart().StartsWith("\"DynamicControlDefinitionJson\": "))
                    {
                        lines.Insert(++i, new string(' ', lines[i].IndexOf('"')) + "\"TemplateDisplayName\": null,");
                    }
                }

                newJson = string.Join(Environment.NewLine, lines);
            }

            return newJson;
        }

        private static void ParseControl(CanvasAppScreen screen, IControl control, string directory, AutoValueExtractor autoValueExtractor)
        {
            autoValueExtractor.PushControl(control.Name);
            Directory.CreateDirectory(directory);
            var sb = new StringBuilder();

            // Write out all Rules
            foreach (var rule in control.Rules)
            {
                autoValueExtractor.Extract(rule);
                sb.AppendLine(rule.Property + "(){"); 
                sb.AppendLine("\t" + rule.InvariantScript.Replace(Environment.NewLine, Environment.NewLine + "\t"));
                sb.AppendLine(EndOfRuleCode + rule.Property + Environment.NewLine);
                rule.InvariantScript = null;
            }

            File.WriteAllText(Path.Join(directory, control.Name) + CodeFileExt, sb.ToString());

            // Create List of Children so the order can be maintained
            var childrenOrder = new List<ChildOrder>(); 

            // Write out all Children Rules
            foreach (var child in control.Children)
            {
                ParseControl(screen, child, Path.Combine(directory, child.Name), autoValueExtractor);
                childrenOrder.Add(new ChildOrder{Name = child.Name, ChildrenOrder = child.ChildrenOrder});
            }

            if (control.Template?.ComponentDefinitionInfo?.Children != null)
            {
                autoValueExtractor.ExtractComponentChildren(control.Template.ComponentDefinitionInfo.Children);
            }

            control.Children = null;
            control.ChildrenOrder = childrenOrder.Count == 0 ? null : childrenOrder;

            autoValueExtractor.Extract(control);
            File.WriteAllText(Path.Combine(directory, Path.GetFileName(directory)) + DataFileExt, 
                              screen.TopParent == control
                                  ? screen.Serialize(Formatting.Indented)
                                  : control.Serialize(Formatting.Indented));

            autoValueExtractor.PopControl();
        }
    }
}
