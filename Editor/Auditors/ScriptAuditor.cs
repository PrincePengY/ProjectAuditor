using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Unity.ProjectAuditor.Editor.CodeAnalysis;
using Unity.ProjectAuditor.Editor.InstructionAnalyzers;
using Unity.ProjectAuditor.Editor.Utils;
using UnityEngine;
using UnityEngine.Profiling;
using Attribute = Unity.ProjectAuditor.Editor.InstructionAnalyzers.Attribute;
using ThreadPriority = System.Threading.ThreadPriority;

namespace Unity.ProjectAuditor.Editor.Auditors
{
    public class ScriptAuditor : IAuditor
    {
        private readonly ProjectAuditorConfig m_Config;
        private readonly List<IInstructionAnalyzer> m_InstructionAnalyzers = new List<IInstructionAnalyzer>();
        private readonly List<OpCode> m_OpCodes = new List<OpCode>();
        private List<ProblemDescriptor> m_ProblemDescriptors;

        private Thread m_AssemblyAnalysisThread;

        internal ScriptAuditor(ProjectAuditorConfig config)
        {
            m_Config = config;
        }

        public IEnumerable<ProblemDescriptor> GetDescriptors()
        {
            return m_ProblemDescriptors;
        }

        public void Audit(Action<ProjectIssue> onIssueFound, Action onComplete, IProgressBar progressBar = null)
        {
            if (m_Config.enableBackgroundAnalysis && m_AssemblyAnalysisThread != null)
                m_AssemblyAnalysisThread.Join();

            var compilationHelper = new AssemblyCompilationHelper();
            var callCrawler = new CallCrawler();

            Profiler.BeginSample("ScriptAuditor.Audit.Compilation");
            var assemblyInfos = compilationHelper.Compile(progressBar);
            Profiler.EndSample();

            var issues = new List<ProjectIssue>();
            var localAssemblyInfos = assemblyInfos.Where(info => !info.readOnly).ToArray();
            var readOnlyAssemblyInfos = assemblyInfos.Where(info => info.readOnly).ToArray();

            var onCallFound = new Action<CallInfo>(pair =>
            {
                callCrawler.Add(pair);
            });

            var onCompleteInternal = new Action<IProgressBar>(bar =>
            {
                compilationHelper.Dispose();
                callCrawler.BuildCallHierarchies(issues, bar);
                onComplete();
            });

            var onIssueFoundInternal = new Action<ProjectIssue>(issue =>
            {
                issues.Add(issue);
                onIssueFound(issue);
            });

            Profiler.BeginSample("ScriptAuditor.Audit.Analysis");

            // first phase: analyze assemblies generated from editable scripts
            AnalyzeAssemblies(localAssemblyInfos, onCallFound, onIssueFoundInternal, null, progressBar);

            var enableBackgroundAnalysis = m_Config.enableBackgroundAnalysis;
#if !UNITY_2019_3_OR_NEWER
            enableBackgroundAnalysis = false;
#endif
            // second phase: analyze all remaining assemblies, in a separate thread if enableBackgroundAnalysis is enabled
            if (enableBackgroundAnalysis)
            {
                m_AssemblyAnalysisThread = new Thread(() =>
                    AnalyzeAssemblies(readOnlyAssemblyInfos, onCallFound, onIssueFound, onCompleteInternal));
                m_AssemblyAnalysisThread.Name = "Assembly Analysis";
                m_AssemblyAnalysisThread.Priority = ThreadPriority.BelowNormal;
                m_AssemblyAnalysisThread.Start();
            }
            else
            {
                Profiler.BeginSample("ScriptAuditor.Audit.AnalysisReadOnly");
                AnalyzeAssemblies(readOnlyAssemblyInfos, onCallFound, onIssueFoundInternal, onCompleteInternal, progressBar);
                Profiler.EndSample();
            }
            Profiler.EndSample();
        }

        private void AnalyzeAssemblies(IEnumerable<AssemblyInfo> assemblyInfos, Action<CallInfo> onCallFound, Action<ProjectIssue> onIssueFound, Action<IProgressBar> onComplete, IProgressBar progressBar = null)
        {
            var compiledAssemblyDirectories = assemblyInfos.Select(info => Path.GetDirectoryName(info.path)).Distinct();

            using (var assemblyResolver = new DefaultAssemblyResolver())
            {
                foreach (var dir in AssemblyHelper.GetPrecompiledAssemblyDirectories())
                    assemblyResolver.AddSearchDirectory(dir);

                foreach (var dir in AssemblyHelper.GetPrecompiledEngineAssemblyDirectories())
                    assemblyResolver.AddSearchDirectory(dir);

                foreach (var dir in compiledAssemblyDirectories)
                    assemblyResolver.AddSearchDirectory(dir);

                if (progressBar != null)
                    progressBar.Initialize("Analyzing Scripts", "Analyzing project scripts",
                        assemblyInfos.Count());

                // Analyse all Player assemblies
                foreach (var assemblyInfo in assemblyInfos)
                {
                    if (progressBar != null)
                        progressBar.AdvanceProgressBar(string.Format("Analyzing {0}", assemblyInfo.name));

                    if (!File.Exists(assemblyInfo.path))
                    {
                        Debug.LogError(assemblyInfo.path + " not found.");
                        continue;
                    }

                    AnalyzeAssembly(assemblyInfo, assemblyResolver, onCallFound, onIssueFound);
                }
            }

            if (progressBar != null)
                progressBar.ClearProgressBar();

            if (onComplete != null)
            {
                onComplete(progressBar);
            }
        }

        public void LoadDatabase(string path)
        {
            m_ProblemDescriptors = ProblemDescriptorHelper.LoadProblemDescriptors(path, "ApiDatabase");

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                foreach (var type in GetAnalyzerTypes(assembly))
                    AddAnalyzer(Activator.CreateInstance(type, this) as IInstructionAnalyzer);
        }

        public IEnumerable<Type> GetAnalyzerTypes(Assembly assembly)
        {
            foreach (var type in assembly.GetTypes())
                if (type.GetCustomAttributes(typeof(Attribute), true).Length > 0)
                    yield return type;
        }

        public void RegisterDescriptor(ProblemDescriptor descriptor)
        {
            // TODO: check for id conflict
            m_ProblemDescriptors.Add(descriptor);
        }

        private void AnalyzeAssembly(AssemblyInfo assemblyInfo, IAssemblyResolver assemblyResolver, Action<CallInfo> onCallFound, Action<ProjectIssue> onIssueFound)
        {
            Profiler.BeginSample("ScriptAuditor.AnalyzeAssembly " + assemblyInfo.name);

            using (var assembly = AssemblyDefinition.ReadAssembly(assemblyInfo.path,
                new ReaderParameters {ReadSymbols = true, AssemblyResolver = assemblyResolver}))
            {
                var assemblyName = assembly.Name.Name;
                //   var packageInfo = AssemblyHelper.GetPackageInfoFromAssembly(assemblyName);

                foreach (var methodDefinition in MonoCecilHelper.AggregateAllTypeDefinitions(assembly.MainModule.Types)
                         .SelectMany(t => t.Methods))
                {
                    if (!methodDefinition.HasBody)
                        continue;

                    AnalyzeMethodBody(assemblyInfo, methodDefinition, onCallFound, onIssueFound);
                }
            }

            Profiler.EndSample();
        }

        private void AnalyzeMethodBody(AssemblyInfo assemblyInfo, MethodDefinition caller, Action<CallInfo> onCallFound, Action<ProjectIssue> onIssueFound)
        {
            if (!caller.DebugInformation.HasSequencePoints)
                return;

            Profiler.BeginSample("ScriptAuditor.AnalyzeMethodBody");

            var callerNode = new CallTreeNode(caller);
            var perfCriticalContext = IsPerformanceCriticalContext(caller);

            foreach (var inst in caller.Body.Instructions.Where(i => m_OpCodes.Contains(i.OpCode)))
            {
                SequencePoint s = null;
                for (var i = inst; i != null; i = i.Previous)
                {
                    s = caller.DebugInformation.GetSequencePoint(i);
                    if (s != null)
                        break;
                }

                Location location = null;
                if (s != null)
                {
                    location = new Location(AssemblyHelper.ResolveAssetPath(assemblyInfo, s.Document.Url), s.StartLine);
                    callerNode.location = location;
                }
                else
                {
                    // sequence point not found. Assuming caller.IsHideBySig == true
                }

                if (inst.OpCode == OpCodes.Call || inst.OpCode == OpCodes.Callvirt)
                {
                    onCallFound(new CallInfo
                    {
                        callee = (MethodReference)inst.Operand,
                        caller = caller,
                        location = location,
                        perfCriticalContext = perfCriticalContext
                    });
                }

                foreach (var analyzer in m_InstructionAnalyzers)
                    if (analyzer.GetOpCodes().Contains(inst.OpCode))
                    {
                        var projectIssue = analyzer.Analyze(caller, inst);
                        if (projectIssue != null)
                        {
                            projectIssue.callTree.perfCriticalContext = perfCriticalContext;
                            projectIssue.callTree.AddChild(callerNode);
                            projectIssue.location = location;
                            projectIssue.assembly = assemblyInfo.name;

                            onIssueFound(projectIssue);
                        }
                    }
            }
            Profiler.EndSample();
        }

        private void AddAnalyzer(IInstructionAnalyzer analyzer)
        {
            m_InstructionAnalyzers.Add(analyzer);
            m_OpCodes.AddRange(analyzer.GetOpCodes());
        }

        public static IEnumerable<ProjectIssue> FindScriptIssues(ProjectReport projectReport, string relativePath)
        {
            return projectReport.GetIssues(IssueCategory.ApiCalls).Where(i => i.relativePath.Equals(relativePath));
        }

        private static bool IsPerformanceCriticalContext(MethodDefinition methodDefinition)
        {
            if (MonoBehaviourAnalysis.IsMonoBehaviour(methodDefinition.DeclaringType) &&
                MonoBehaviourAnalysis.IsMonoBehaviourUpdateMethod(methodDefinition))
                return true;
            if (ComponentSystemAnalysis.IsComponentSystem(methodDefinition.DeclaringType) &&
                ComponentSystemAnalysis.IsOnUpdateMethod(methodDefinition))
                return true;
            return false;
        }
    }
}
