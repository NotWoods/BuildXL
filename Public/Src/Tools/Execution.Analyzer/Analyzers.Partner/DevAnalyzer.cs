// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BuildXL.Pips;
using BuildXL.Pips.Graph;
using BuildXL.Pips.Operations;
using BuildXL.Scheduler.Tracing;
using BuildXL.Utilities;

namespace BuildXL.Execution.Analyzer
{
    internal partial class Args
    {
        public Analyzer InitializeDevAnalyzer()
        {
            string outputFilePath = null;
            long? pipId = null;

            foreach (var opt in AnalyzerOptions)
            {
                if (opt.Name.Equals("out", StringComparison.OrdinalIgnoreCase) ||
                   opt.Name.Equals("o", StringComparison.OrdinalIgnoreCase))
                {
                    outputFilePath = ParseSingletonPathOption(opt, outputFilePath);
                }
                else if (opt.Name.StartsWith("pip", StringComparison.OrdinalIgnoreCase) ||
                    opt.Name.StartsWith("p", StringComparison.OrdinalIgnoreCase))
                {
                    pipId = ParseSemistableHash(opt);
                }
                else
                {
                    throw Error("Unknown option for fingerprint text analysis: {0}", opt.Name);
                }
            }

            if (string.IsNullOrWhiteSpace(outputFilePath))
            {
                throw Error("Output file must be specified with /out");
            }

            return new DevAnalyzer(GetAnalysisInput())
            {
                OutputFilePath = outputFilePath,
                PipSemistableHash = pipId,
            };
        }
    }

    /// <summary>
    /// Placeholder analyzer for adding custom analyzer on demand for analyzing issues
    /// </summary>
    internal sealed class DevAnalyzer : Analyzer
    {
        /// <summary>
        /// The path to the fingerprint file
        /// </summary>
        public string OutputFilePath;

        private StreamWriter m_writer;

        /// <summary>
        /// The target pip id (if any)
        /// </summary>
        public long? PipSemistableHash;

        private readonly HashSet<PipId> m_producers = new HashSet<PipId>();

        public AbsolutePath SpecPath { get; private set; }

        public AbsolutePath DirectoryPath { get; private set; }

        public DevAnalyzer(AnalysisInput input)
            : base(input)
        {
        }

        public override void Prepare()
        {
            m_writer = new StreamWriter(OutputFilePath);

            var path = AbsolutePath.Create(PathTable, @"D:\dbs\el\omr\Build\DominoTemp\Obj\3\a\boyzvbglg4tncpv23bkcqbww\dropd-addartifacts\dropd-addartifacts-stdout.txt");

            var producers = PipGraph.GetProducingPips(path).ToArray();

            var ipcPip = producers.OfType<IpcPip>().FirstOrDefault();

            var outputFile = ipcPip.OutputFile.Path;
            bool isOutput = path == outputFile;

            bool hasConsumers = HasRealConsumers(ipcPip);

            var consumers = PipGraph.GetConsumingPips(path).ToArray();

            var dependents = PipGraph.RetrievePipImmediateDependents(ipcPip);


            // GetPathProducers(path);
        }

        public bool HasRealConsumers(IpcPip pip)
        {
            foreach (var outgoingEdge in PipGraph.DataflowGraph.GetOutgoingEdges(pip.PipId.ToNodeId()))
            {
                var otherPipId = outgoingEdge.OtherNode.ToPipId();
                if (PipGraph.PipTable.GetServiceInfo(otherPipId).Kind == ServicePipKind.None)
                {
                    var other = GetPip(otherPipId);
                    return true;
                }
            }

            return false;
        }

        public override void FileArtifactContentDecided(FileArtifactContentDecidedEventData data)
        {
            if (data.FileArtifact.Path == SpecPath)
            {
                m_writer.WriteLine("Hash: {0}", data.FileContentInfo.Hash.ToString());
                m_writer.WriteLine("Size: {0}", data.FileContentInfo.Length);
                m_writer.WriteLine();
            }
        }

        public override void DirectoryMembershipHashed(DirectoryMembershipHashedEventData data)
        {
            if (data.Directory == DirectoryPath)
            {
                m_writer.WriteLine("Directory Membership");
                foreach (var member in data.Members)
                {
                    m_writer.WriteLine(member.ToString(PathTable));
                }

                m_writer.WriteLine();
            }
        }

        private void GetProcessDirectoryDependenciesContents()
        {
            Process process = null;
            foreach (var pipId in PipTable.StableKeys)
            {
                var possibleMatch = PipTable.GetPipSemiStableHash(pipId);
                if (possibleMatch == PipSemistableHash)
                {
                    process = (Process)GetPip(pipId);
                }
            }

            foreach (var directory in process.DirectoryDependencies)
            {
                m_writer.WriteLine(directory.Path.ToString(PathTable));
                foreach (var file in PipGraph.ListSealedDirectoryContents(directory))
                {
                    m_writer.WriteLine(file.Path.ToString(PathTable));
                }

                m_writer.WriteLine();
            }

            m_writer.Flush();
        }

        private void GetPathProducers(AbsolutePath path)
        {
            Dictionary<AbsolutePath, DirectoryArtifact> directories = new Dictionary<AbsolutePath, DirectoryArtifact>();
            foreach (var directory in PipGraph.AllSealDirectories)
            {
                directories[directory.Path] = directory;
            }

            var latest = PipGraph.TryGetLatestFileArtifactForPath(path);
            if (!latest.IsValid)
            {
                m_writer.WriteLine("No declared file producers");
            }
            else
            {
                for (int i = 0; i <= latest.RewriteCount; i++)
                {
                    var producerId = PipGraph.TryGetProducer(new FileArtifact(path, i));
                    if (producerId.IsValid)
                    {
                        m_writer.WriteLine("File Producer: ({0})", i);
                        m_producers.Add(producerId);
                        m_writer.WriteLine(GetDescription(GetPip(producerId)));
                        m_writer.WriteLine();
                    }
                }
            }

            while (path.IsValid)
            {
                DirectoryArtifact containingDirectory;
                if (directories.TryGetValue(path, out containingDirectory))
                {
                    var directoryNode = PipGraph.GetSealedDirectoryNode(containingDirectory);
                    var sealDirectory = (SealDirectory)GetPip(directoryNode);
                    if (sealDirectory.Kind == SealDirectoryKind.Opaque)
                    {
                        m_writer.WriteLine("Directory Producer:");
                        m_producers.Add(PipGraph.GetProducer(containingDirectory));
                        m_writer.WriteLine(GetDescription(GetPip(PipGraph.GetProducer(containingDirectory))));
                        m_writer.WriteLine("Directory: " + GetDescription(sealDirectory));
                        m_writer.WriteLine();
                    }
                }

                path = path.GetParent(PathTable);
            }
        }

        public override void PipExecutionStepPerformanceReported(PipExecutionStepPerformanceEventData data)
        {
            if (m_producers.Contains(data.PipId))
            {
                m_writer.WriteLine("Step: " + data.Step);
                m_writer.WriteLine("Start: " + data.StartTime);
                m_writer.WriteLine("Duration: " + data.Duration);
                m_writer.WriteLine();
            }

            base.PipExecutionStepPerformanceReported(data);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2202:DoNotDisposeObjectsMultipleTimes")]
        public override int Analyze()
        {
            // GetNestedSealDirectories();
            m_writer.Dispose();

            return 0;
        }

        private void GetNestedSealDirectories()
        {
            Dictionary<AbsolutePath, DirectoryArtifact> directories = new Dictionary<AbsolutePath, DirectoryArtifact>();
            foreach (var directory in PipGraph.AllSealDirectories)
            {
                directories[directory.Path] = directory;
            }

            foreach (var directory in PipGraph.AllSealDirectories)
            {
                var path = directory.Path;
                var current = path;
                while (current.IsValid)
                {
                    DirectoryArtifact containerArtifact;
                    if (current != path && directories.TryGetValue(current, out containerArtifact))
                    {
                        var innerNode = PipGraph.GetSealedDirectoryNode(directory);
                        var containerNode = PipGraph.GetSealedDirectoryNode(containerArtifact);
                        m_writer.WriteLine($"Path: {path.ToString(PathTable)}");
                        m_writer.WriteLine($"Child: {GetSealDescription(GetPip(innerNode))}");
                        m_writer.WriteLine($"Container Path: {containerArtifact.Path.ToString(PathTable)}");
                        m_writer.WriteLine($"Container: {GetSealDescription(GetPip(containerNode))}");
                        m_writer.WriteLine();
                    }

                    current = current.GetParent(PathTable);
                }
            }
        }

        private string GetSealDescription(Pip pip)
        {
            SealDirectory seal = (SealDirectory)pip;
            return $"[{seal.Kind}] {GetDescription(pip)}";
        }

        public override void Dispose()
        {
            m_writer.Dispose();
        }
    }
}
