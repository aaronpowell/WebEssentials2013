﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.CSS.Core;
using Microsoft.Less.Core;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;

namespace MadsKristensen.EditorExtensions.Helpers
{
    ///<summary>Maintains a graph of dependencies among files in the solution.</summary>
    /// <remarks>This class is completely decoupled from all Visual Studio concepts.</remarks>
    public abstract class DependencyGraph
    {
        // This dictionary and graph also contains nodes
        // for files that do not (yet) exist, as well as
        // files that have been deleted.  This allows us
        // to handle such files after they're recreated,
        // including untouched imports from other files.
        // In other words, you can never remove from the
        // graph, except by clearing & re-scanning.
        // This is synchronized by rwLock.
        readonly Dictionary<string, GraphNode> nodes = new Dictionary<string, GraphNode>(StringComparer.OrdinalIgnoreCase);
        readonly AsyncReaderWriterLock rwLock = new AsyncReaderWriterLock();

        #region Graph Consumption
        ///<summary>Gets all files that directly depend on the specified file.</summary>
        public async Task<IEnumerable<string>> GetDirectDependentsAsync(string fileName)
        {
            fileName = Path.GetFullPath(fileName);

            using (await rwLock.ReadLockAsync())
            {
                GraphNode fileNode;
                if (!nodes.TryGetValue(fileName, out fileNode))
                    return Enumerable.Empty<string>();
                return fileNode.Dependents.Select(d => d.FileName);
            }
        }
        ///<summary>Gets all files that indirectly depend on the specified file.</summary>
        public async Task<IEnumerable<string>> GetRecursiveDependentsAsync(string fileName)
        {
            HashSet<GraphNode> visited;
            fileName = Path.GetFullPath(fileName);
            using (await rwLock.ReadLockAsync())
            {
                GraphNode rootNode;
                if (!nodes.TryGetValue(fileName, out rootNode))
                    return Enumerable.Empty<string>();

                var stack = new Stack<GraphNode>();
                stack.Push(rootNode);
                visited = new HashSet<GraphNode> { rootNode };
                while (stack.Count > 0)
                {
                    foreach (var child in stack.Pop().Dependents)
                    {
                        if (!visited.Add(child)) continue;
                        stack.Push(child);
                    }
                }
                // Don't return the original file.
                visited.Remove(rootNode);
            }
            return visited.Select(n => n.FileName);
        }
        private class GraphNode
        {
            // Protected by parent graph's rwLock
            readonly HashSet<GraphNode> dependencies = new HashSet<GraphNode>();
            readonly HashSet<GraphNode> dependents = new HashSet<GraphNode>();

            public string FileName { get; private set; }

            // The LINQ Contains() extension method will call into the underlying HashSet<T>.

            ///<summary>Gets the nodes that this file depends on.</summary>
            public IEnumerable<GraphNode> Dependencies { get { return dependencies; } }
            ///<summary>Gets the nodes that depend on this file.</summary>
            public IEnumerable<GraphNode> Dependents { get { return dependents; } }

            public GraphNode(string fileName)
            {
                FileName = fileName;
            }

            ///<summary>Marks this node as depending on the specified node, adding an edge to the graph if one does not exist already.</summary>
            ///<returns>True if an edge was added; false if the dependency already existed.</returns>
            public bool AddDependency(GraphNode node)
            {
                if (!dependencies.Add(node))
                    return false;
                node.dependents.Add(this);
                return true;
            }

            ///<summary>Removes all edges for nodes that this file depends on.  Call this method, inside a write lock, before reparsing the file.</summary>
            public void ClearDependencies()
            {
                foreach (var child in Dependencies)
                    child.dependents.Remove(this);
                dependencies.Clear();
            }
        }
        #endregion

        #region Graph Creation
        ///<summary>Gets the full paths to all files that the given file depends on.  (the dependencies need not exist).</summary>
        ///<remarks>This method will be called concurrently on arbitrary threads.</remarks>
        protected abstract IEnumerable<string> GetDependencyPaths(string fileName);
        ///<summary>Rescans the entire graph from a collection of source files, replacing the entire graph.</summary>
        ///<remarks>Although this method is async, it performs lots of synchronous work, and should not be called on a UI thread.</remarks>
        public async Task RescanAllAsync(IEnumerable<string> sourceFiles)
        {
            // Parse all of the files in the background, then update the dictionary on one thread
            var dependencies = sourceFiles
                .AsParallel()
                .Select(f => new { FileName = f, dependencies = GetDependencyPaths(f) });
            using (await rwLock.WriteLockAsync())
            {
                nodes.Clear();
                foreach (var item in dependencies)
                {
                    var parentNode = GetNode(item.FileName);
                    foreach (var dependency in item.dependencies)
                        parentNode.AddDependency(GetNode(dependency));
                }
            }
#if false
            nodes.Clear();
            Parallel.ForEach(
                sourceFiles,
                () => ImmutableStack<Tuple<string, IEnumerable<string>>>.Empty,
                (filename, state, stack) => stack.Push(Tuple.Create(filename, GetDependencyPaths(filename))),
                stack =>
                {
                    foreach (var item in stack)
                    {
                        var parentNode = GetNode(item.Item1);
                        foreach (var dependency in item.Item2)
                            parentNode.AddDependency(GetNode(dependency));
                    }
                }
             );
#endif
        }
        GraphNode GetNode(string filename)
        {
            bool unused;
            return GetNode(filename, out unused);
        }
        GraphNode GetNode(string filename, out bool created)
        {
            filename = Path.GetFullPath(filename);
            GraphNode node;
            created = nodes.TryGetValue(filename, out node);
            if (created)
                nodes.Add(filename, node = new GraphNode(filename));
            return node;
        }

        ///<summary>Reparses dependencies for a single file and updates the graph.</summary>
        ///<remarks>Although this method is async, it performs synchronous work, and should not be called on a UI thread.</remarks>
        public Task RescanFileAsync(string fileName) { return RescanFileAsync(fileName, hasLock: false); }
        private async Task RescanFileAsync(string fileName, bool hasLock)
        {
            fileName = Path.GetFullPath(fileName);
            var dependencies = GetDependencyPaths(fileName);
            using (hasLock ? new AsyncReaderWriterLock.Releaser() : await rwLock.WriteLockAsync())
            {
                var parentNode = GetNode(fileName);
                bool created;
                foreach (var dependency in dependencies)
                {
                    var childNode = GetNode(dependency, out created);
                    parentNode.AddDependency(childNode);
                    if (created)    // This will (and must) run synchronously, since it doesn't acquire the lock
                        await RescanFileAsync(childNode.FileName, hasLock: true);
                }
            }
        }
        #endregion
    }
    ///<summary>A DependencyGraph that reads Visual Studio solutions.</summary>
    public abstract class VsDependencyGraph : DependencyGraph
    {
        readonly ISet<string> extensions;
        ///<summary>Gets the ContentType of the files analyzed by this instance.</summary>
        public IContentType ContentType { get; private set; }

        private readonly DocumentEvents documentEvents = EditorExtensionsPackage.DTE.Events.DocumentEvents;
        private readonly SolutionEvents solutionEvents = EditorExtensionsPackage.DTE.Events.SolutionEvents;
        private readonly ProjectItemsEvents projectItemEvents = ((Events2)EditorExtensionsPackage.DTE.Events).ProjectItemsEvents;

        protected VsDependencyGraph(IContentType contentType, IFileExtensionRegistryService fileExtensionRegistry)
        {
            ContentType = contentType;
            extensions = fileExtensionRegistry.GetFileExtensionSet(contentType);
        }


        ///<summary>Rescans all the entire graph from the source files in the current Visual Studio solution.</summary>
        ///<remarks>Although this method is async, it performs lots of synchronous work, and should not be called on a UI thread.</remarks>
        public Task RescanSolutionAsync()
        {
            var sourceFiles = ProjectHelpers.GetAllProjects()
                .Select(ProjectHelpers.GetRootFolder)
                .Where(p => !string.IsNullOrEmpty(p))
                .SelectMany(p => Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                .Where(f => extensions.Contains(Path.GetExtension(f)));
            return RescanAllAsync(sourceFiles);
        }

        bool isEnabled;
        public bool IsEnabled
        {
            get { return isEnabled; }
            set
            {
                if (isEnabled == value) return;
                isEnabled = value;
                if (value)
                {
                    AddEventHandlers();
                    if (EditorExtensionsPackage.DTE.Solution.IsOpen)
                        Task.Run(() => RescanSolutionAsync()).DontWait("scanning solution for " + ContentType + " dependencies");
                }
                else
                    RemoveEventHandlers();
            }
        }

        private void AddEventHandlers()
        {
            solutionEvents.Opened += SolutionEvents_Opened;
            solutionEvents.ProjectAdded += SolutionEvents_ProjectAdded;
            projectItemEvents.ItemAdded += ProjectItemEvents_ItemAdded;
            documentEvents.DocumentSaved += DocumentEvents_DocumentSaved;
            projectItemEvents.ItemRenamed += ProjectItemEvents_ItemRenamed;
        }


        private void RemoveEventHandlers()
        {
            solutionEvents.Opened -= SolutionEvents_Opened;
            solutionEvents.ProjectAdded -= SolutionEvents_ProjectAdded;
            projectItemEvents.ItemAdded -= ProjectItemEvents_ItemAdded;
            documentEvents.DocumentSaved -= DocumentEvents_DocumentSaved;
            projectItemEvents.ItemRenamed -= ProjectItemEvents_ItemRenamed;
        }

        #region Event Handlers
        private void ProjectItemEvents_ItemRenamed(ProjectItem ProjectItem, string OldName)
        {
            var fileName = ProjectItem.FileNames[1];
            if (extensions.Contains(Path.GetExtension(fileName)))
                Task.Run(() => RescanFileAsync(fileName)).DontWait("parsing " + ProjectItem.Name + " for dependencies");
        }
        private void DocumentEvents_DocumentSaved(Document Document)
        {
            var fileName = Document.Path;
            if (extensions.Contains(Path.GetExtension(fileName)))
                Task.Run(() => RescanFileAsync(fileName)).DontWait("parsing " + Document.Name + " for dependencies");
        }

        private void ProjectItemEvents_ItemAdded(ProjectItem ProjectItem)
        {
            var fileName = ProjectItem.FileNames[1];
            if (extensions.Contains(Path.GetExtension(fileName)))
                Task.Run(() => RescanFileAsync(fileName)).DontWait("parsing " + ProjectItem.Name + " for dependencies");
        }

        private void SolutionEvents_ProjectAdded(Project Project)
        {
            Task.Run(() => RescanSolutionAsync()).DontWait("scanning solution for " + ContentType + " dependencies");
        }

        private void SolutionEvents_Opened()
        {
            Task.Run(() => RescanSolutionAsync()).DontWait("scanning new solution for " + ContentType + " dependencies");
        }
        #endregion
    }
    public class CssDependencyGraph<TParser> : VsDependencyGraph where TParser : CssParser, new()
    {
        public string Extension { get; private set; }
        public CssDependencyGraph(string extension, IFileExtensionRegistryService fileExtensionRegistry)
            : base(fileExtensionRegistry.GetContentTypeForExtension(extension.TrimStart('.')), fileExtensionRegistry)
        {
            Extension = extension;
        }

        protected override IEnumerable<string> GetDependencyPaths(string fileName)
        {
            return new CssItemAggregator<string> { (ImportDirective i) => i.FileName == null ? i.Url.UrlString.Text : i.FileName.Text }
                            .Crawl(new TParser().Parse(File.ReadAllText(fileName), false))
                            .Select(f => Path.Combine(Path.GetDirectoryName(fileName), f.Trim('"', '\'')))
                            .Select(f => f.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? f : f + Extension);
        }
    }
    [Export]
    public class LessDependencyGraph : CssDependencyGraph<LessParser>
    {
        [ImportingConstructor]
        public LessDependencyGraph(IFileExtensionRegistryService fileExtensionRegistry) : base(".less", fileExtensionRegistry)
        {
        }
    }
}