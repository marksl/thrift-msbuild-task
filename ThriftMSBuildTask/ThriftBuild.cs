/**
 * Licensed to the Apache Software Foundation (ASF) under one
 * or more contributor license agreements. See the NOTICE file
 * distributed with this work for additional information
 * regarding copyright ownership. The ASF licenses this file
 * to you under the Apache License, Version 2.0 (the
 * "License"); you may not use this file except in compliance
 * with the License. You may obtain a copy of the License at
 *
 *   http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing,
 * software distributed under the License is distributed on an
 * "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY
 * KIND, either express or implied. See the License for the
 * specific language governing permissions and limitations
 * under the License.
 *
 * Contains some contributions under the Thrift Software License.
 * Please see doc/old-thrift-license.txt in the Thrift distribution for
 * details.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.Build.Tasks;
using System.IO;
using System.Diagnostics;

namespace ThriftMSBuildTask
{
    /// <summary>
    /// MSBuild Task to generate csharp from .thrift files, and compile the code into a library: ThriftImpl.dll
    /// </summary>
    public class ThriftBuild : Task
    {
        /// <summary>
        /// The full path to the thrift.exe compiler
        /// </summary>
        [Required]
        public ITaskItem ThriftExecutable { get; set; }

        /// <summary>
        /// The full path to a thrift.dll C# library
        /// </summary>
        [Required]
        public ITaskItem ThriftLibrary { get; set; }

        /// <summary>
        /// A directory containing .thrift files
        /// </summary>
        [Required]
        public ITaskItem ThriftDefinitionDir { get; set; }

        /// <summary>
        /// The name of the auto-gen and compiled thrift library. It will placed in
        /// the same directory as ThriftLibrary
        /// </summary>
        [Required]
        public ITaskItem OutputName { get; set; }

        /// <summary>
        /// The name of the AssemblyInfo.cs
        /// </summary>
        public ITaskItem AssemblyInfoPath { get; set; }

        /// <summary>
        /// The full path to the compiled ThriftLibrary. This allows msbuild tasks to use this
        /// output as a variable for use elsewhere.
        /// </summary>
        [Output]
        public ITaskItem ThriftImplementation { get; private set; }

        private const string LastCompilationName = "LAST_COMP_TIMESTAMP";

        private void LogMessage(string text, MessageImportance importance)
        {
            var m = new Message
                        {
                            Text = text,
                            Importance = importance.ToString(),
                            BuildEngine = BuildEngine
                        };
            m.Execute();
        }

        private static List<string> FindSourcesRecursively(string sourceDirectory, List<string> paths = null)
        {
            if (paths == null)
            {
                paths = new List<string>();
            }

            string[] files = Directory.GetFiles(sourceDirectory, "*.cs");
            paths.AddRange(files);

            string[] directories = Directory.GetDirectories(sourceDirectory);

            return directories.Aggregate(paths, (current, dir) => FindSourcesRecursively(dir, current));
        }

        private static ITaskItem[] FindSources(string sourceDirectory, string assemblyInfoPath)
        {
            List<string> files = FindSourcesRecursively(sourceDirectory);

            if (!string.IsNullOrEmpty(assemblyInfoPath))
                files.Add(assemblyInfoPath);

            var items = new ITaskItem[files.Count];
            for (int i = 0; i < items.Length; i++)
            {
                items[i] = new TaskItem(files[i]);
            }
            return items;
        }

        /// <summary>
        /// Quote paths with spaces
        /// </summary>
        private string SafePath(string path)
        {
            if (path.Contains(' ') && !path.StartsWith("\""))
            {
                return "\"" + path + "\"";
            }
            return path;
        }

        private string LastWriteTime(string defDir)
        {
            string[] files = Directory.GetFiles(defDir, "*.thrift");
            DateTime d = (new DirectoryInfo(defDir)).LastWriteTime;
            foreach (string file in files)
            {
                var f = new FileInfo(file);
                DateTime curr = f.LastWriteTime;
                if (DateTime.Compare(curr, d) > 0)
                {
                    d = curr;
                }
            }
            return d.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture);
        }

        public override bool Execute()
        {
            string defDir = SafePath(ThriftDefinitionDir.ItemSpec);
            
            //look for last compilation timestamp
            string lastBuildPath = Path.Combine(defDir, LastCompilationName);
            string lastWrite = LastWriteTime(defDir);

            if (File.Exists(lastBuildPath))
            {
                string lastComp = File.ReadAllText(lastBuildPath);

                var f = new FileInfo(ThriftLibrary.ItemSpec);

                string thriftLibTime = f.LastWriteTimeUtc.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture);
                if (ThriftIsModified(thriftLibTime, lastComp))
                {
                    lastWrite = thriftLibTime;
                }
                else if (ThriftIsUpToDate(thriftLibTime, lastWrite, lastComp))
                {
                    LogMessage("ThriftImpl up-to-date", MessageImportance.High);
                    return true;
                }
            }

            //find the directory of the thriftlibrary (that's where output will go)
            var thriftLibInfo = new FileInfo(ThriftLibraryPath);
            if (thriftLibInfo.Directory == null)
            {
                LogMessage("ThriftLibraryPath directory does not exist.", MessageImportance.High);
                return false;
            }

            string thriftDir = thriftLibInfo.Directory.FullName;

            DeleteGeneratedFolder(thriftDir);

            LogMessage(defDir, MessageImportance.High);

            //run the thrift executable to generate C#
            foreach (string thriftFile in Directory.GetFiles(defDir, "*.thrift"))
            {
                LogMessage("Generating code for: " + thriftFile, MessageImportance.Normal);
                var p = new Process
                            {
                                StartInfo =
                                    {
                                        FileName = SafePath(ThriftExecutable.ItemSpec),
                                        Arguments = "--gen csharp -o " + SafePath(thriftDir) + " -r " + thriftFile,
                                        UseShellExecute = false,
                                        CreateNoWindow = true,
                                        RedirectStandardOutput = false
                                    }
                            };
                p.Start();
                p.WaitForExit();
                if (p.ExitCode != 0)
                {
                    LogMessage("thrift.exe failed to compile " + thriftFile, MessageImportance.High);
                    return false;
                }
                if (p.ExitCode != 0)
                {
                    LogMessage("thrift.exe failed to compile " + thriftFile, MessageImportance.High);
                    return false;
                }
            }

            string outputPath = Path.Combine(thriftDir, OutputName.ItemSpec);

            var csc = new Csc
                          {
                              TargetType = "library",
                              References = new ITaskItem[] {new TaskItem(ThriftLibrary.ItemSpec)},
                              EmitDebugInformation = true,
                              OutputAssembly = new TaskItem(outputPath),
                              Sources = FindSources(Path.Combine(thriftDir, "gen-csharp"), AssemblyInfoPath != null ? AssemblyInfoPath.ItemSpec : null),
                              BuildEngine = this.BuildEngine
                          };
            
            LogMessage("Compiling generated cs...", MessageImportance.Normal);
            if (!csc.Execute())
            {
                return false;
            }

            DeleteGeneratedFolder(thriftDir);

            //write file to defDir to indicate a build was successfully completed
            File.WriteAllText(lastBuildPath, lastWrite);

            ThriftImplementation = new TaskItem(outputPath);

            return true;
        }

        private static bool ThriftIsUpToDate(string thriftLibTime, string lastWrite, string lastComp)
        {
            return lastComp == lastWrite || (lastComp == thriftLibTime && String.CompareOrdinal(lastComp, lastWrite) > 0);
        }

        private static bool ThriftIsModified(string thriftLibTime, string lastComp)
        {
            return String.CompareOrdinal(lastComp, thriftLibTime) < 0;
        }

        private static void DeleteGeneratedFolder(string thriftDir)
        {
            string genDir = Path.Combine(thriftDir, "gen-csharp");
            if (Directory.Exists(genDir))
            {
                try
                {
                    Directory.Delete(genDir, true);
                }
                catch
                {
                    /*eh i tried, just over-write now*/
                }
            }
        }

        private string ThriftLibraryPath
        {
            get { return SafePath(ThriftLibrary.ItemSpec); }
        }
    }
}