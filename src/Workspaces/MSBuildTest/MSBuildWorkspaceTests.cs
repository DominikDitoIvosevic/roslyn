﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.MSBuild.Build;
using Microsoft.CodeAnalysis.Test.Utilities;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.UnitTests;
using Microsoft.CodeAnalysis.UnitTests.TestFiles;
using Roslyn.Test.Utilities;
using Roslyn.Utilities;
using Xunit;
using static Microsoft.CodeAnalysis.MSBuild.UnitTests.SolutionGeneration;
using CS = Microsoft.CodeAnalysis.CSharp;

namespace Microsoft.CodeAnalysis.MSBuild.UnitTests
{
    public class MSBuildWorkspaceTests : MSBuildWorkspaceTestBase
    {
        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public void TestCreateMSBuildWorkspace()
        {
            using (var workspace = CreateMSBuildWorkspace())
            {
                Assert.NotNull(workspace);
                Assert.NotNull(workspace.Services);
                Assert.NotNull(workspace.Services.Workspace);
                Assert.Equal(workspace, workspace.Services.Workspace);
                Assert.NotNull(workspace.Services.HostServices);
                Assert.NotNull(workspace.Services.PersistentStorage);
                Assert.NotNull(workspace.Services.TemporaryStorage);
                Assert.NotNull(workspace.Services.TextFactory);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenSolution_SingleProjectSolution()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.Projects.First();
                var document = project.Documents.First();
                var tree = await document.GetSyntaxTreeAsync();
                var type = tree.GetRoot().DescendantTokens().First(t => t.ToString() == "class").Parent;
                Assert.NotNull(type);
                Assert.StartsWith("public class CSharpClass", type.ToString(), StringComparison.Ordinal);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(2824, "https://github.com/dotnet/roslyn/issues/2824")]
        public async Task Test_OpenProjectReferencingPortableProject()
        {
            var files = new FileSet(
                (@"CSharpProject\ReferencesPortableProject.csproj", Resources.ProjectFiles.CSharp.ReferencesPortableProject),
                (@"CSharpProject\Program.cs", Resources.SourceFiles.CSharp.CSharpClass),
                (@"CSharpProject\PortableProject.csproj", Resources.ProjectFiles.CSharp.PortableProject),
                (@"CSharpProject\CSharpClass.cs", Resources.SourceFiles.CSharp.CSharpClass));

            CreateFiles(files);

            var projectFilePath = GetSolutionFileName(@"CSharpProject\ReferencesPortableProject.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projectFilePath);
                AssertFailures(workspace);

                var hasFacades = project.MetadataReferences.OfType<PortableExecutableReference>().Any(r => r.FilePath.Contains("Facade"));
                Assert.True(hasFacades, userMessage: "Expected to find facades in the project references:" + Environment.NewLine +
                    string.Join(Environment.NewLine, project.MetadataReferences.OfType<PortableExecutableReference>().Select(r => r.FilePath)));
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task Test_SharedMetadataReferences()
        {
            CreateFiles(GetMultiProjectSolutionFiles());
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var p0 = solution.Projects.ElementAt(0);
                var p1 = solution.Projects.ElementAt(1);

                Assert.NotSame(p0, p1);

                var p0mscorlib = GetMetadataReference(p0, "mscorlib");
                var p1mscorlib = GetMetadataReference(p1, "mscorlib");

                Assert.NotNull(p0mscorlib);
                Assert.NotNull(p1mscorlib);

                // metadata references to mscorlib in both projects are the same
                Assert.Same(p0mscorlib, p1mscorlib);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(546171, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/546171")]
        public async Task Test_SharedMetadataReferencesWithAliases()
        {
            var projPath1 = @"CSharpProject\CSharpProject_ExternAlias.csproj";
            var projPath2 = @"CSharpProject\CSharpProject_ExternAlias2.csproj";

            var files = new FileSet(
                (projPath1, Resources.ProjectFiles.CSharp.ExternAlias),
                (projPath2, Resources.ProjectFiles.CSharp.ExternAlias2),
                (@"CSharpProject\CSharpExternAlias.cs", Resources.SourceFiles.CSharp.CSharpExternAlias));

            CreateFiles(files);

            var fullPath1 = GetSolutionFileName(projPath1);
            var fullPath2 = GetSolutionFileName(projPath2);
            using (var workspace = CreateMSBuildWorkspace())
            {
                var proj1 = await workspace.OpenProjectAsync(fullPath1);
                var proj2 = await workspace.OpenProjectAsync(fullPath2);

                var p1Sys1 = GetMetadataReferenceByAlias(proj1, "Sys1");
                var p1Sys2 = GetMetadataReferenceByAlias(proj1, "Sys2");
                var p2Sys1 = GetMetadataReferenceByAlias(proj2, "Sys1");
                var p2Sys3 = GetMetadataReferenceByAlias(proj2, "Sys3");

                Assert.NotNull(p1Sys1);
                Assert.NotNull(p1Sys2);
                Assert.NotNull(p2Sys1);
                Assert.NotNull(p2Sys3);

                // same filepath but different alias so they are not the same instance
                Assert.NotSame(p1Sys1, p1Sys2);
                Assert.NotSame(p2Sys1, p2Sys3);

                // same filepath and alias so they are the same instance
                Assert.Same(p1Sys1, p2Sys1);

                var mdp1Sys1 = GetMetadata(p1Sys1);
                var mdp1Sys2 = GetMetadata(p1Sys2);
                var mdp2Sys1 = GetMetadata(p2Sys1);
                var mdp2Sys3 = GetMetadata(p2Sys1);

                Assert.NotNull(mdp1Sys1);
                Assert.NotNull(mdp1Sys2);
                Assert.NotNull(mdp2Sys1);
                Assert.NotNull(mdp2Sys3);

                // all references to System.dll share the same metadata bytes
                Assert.Same(mdp1Sys1.Id, mdp1Sys2.Id);
                Assert.Same(mdp1Sys1.Id, mdp2Sys1.Id);
                Assert.Same(mdp1Sys1.Id, mdp2Sys3.Id);
            }
        }

        private static MetadataReference GetMetadataReference(Project project, string name)
            => project.MetadataReferences
                .OfType<PortableExecutableReference>()
                .SingleOrDefault(mr => mr.FilePath.Contains(name));

        private static MetadataReference GetMetadataReferenceByAlias(Project project, string aliasName)
            => project.MetadataReferences
                .OfType<PortableExecutableReference>()
                .SingleOrDefault(mr =>
                    !mr.Properties.Aliases.IsDefault &&
                    mr.Properties.Aliases.Contains(aliasName));

        private static Metadata GetMetadata(MetadataReference metadataReference)
            => ((PortableExecutableReference)metadataReference).GetMetadata();

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(552981, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/552981")]
        public async Task TestOpenSolution_DuplicateProjectGuids()
        {
            CreateFiles(GetSolutionWithDuplicatedGuidFiles());
            var solutionFilePath = GetSolutionFileName("DuplicatedGuids.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(831379, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/831379")]
        public async Task GetCompilationWithCircularProjectReferences()
        {
            CreateFiles(GetSolutionWithCircularProjectReferences());
            var solutionFilePath = GetSolutionFileName("CircularSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);

                // Verify we can get compilations for both projects
                var projects = solution.Projects.ToArray();

                // Exactly one of them should have a reference to the other. Which one it is, is unspecced
                Assert.True(projects[0].ProjectReferences.Any(r => r.ProjectId == projects[1].Id) ||
                            projects[1].ProjectReferences.Any(r => r.ProjectId == projects[0].Id));

                var compilation1 = await projects[0].GetCompilationAsync();
                var compilation2 = await projects[1].GetCompilationAsync();

                // Exactly one of them should have a compilation to the other. Which one it is, is unspecced
                Assert.True(compilation1.References.OfType<CompilationReference>().Any(c => c.Compilation == compilation2) ||
                            compilation2.References.OfType<CompilationReference>().Any(c => c.Compilation == compilation1));
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestInternalsVisibleToSigned()
        {
            var solution = await SolutionAsync(
                Project(
                    ProjectName("Project1"),
                    Sign,
                    Document(string.Format(
@"using System.Runtime.CompilerServices;
[assembly:InternalsVisibleTo(""Project2, PublicKey={0}"")]
class C1
{{
}}", PublicKey))),
                Project(
                    ProjectName("Project2"),
                    Sign,
                    ProjectReference("Project1"),
                    Document(@"class C2 : C1 { }")));

            var project2 = solution.GetProjectsByName("Project2").First();
            var compilation = await project2.GetCompilationAsync();
            var diagnostics = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error || d.Severity == DiagnosticSeverity.Warning)
                .ToArray();

            Assert.Empty(diagnostics);
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestVersions()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var sversion = solution.Version;
                var latestPV = solution.GetLatestProjectVersion();
                var project = solution.Projects.First();
                var pversion = project.Version;
                var document = project.Documents.First();
                var dversion = await document.GetTextVersionAsync();
                var latestDV = await project.GetLatestDocumentVersionAsync();

                // update document
                var solution1 = solution.WithDocumentText(document.Id, SourceText.From("using test;"));
                var document1 = solution1.GetDocument(document.Id);
                var dversion1 = await document1.GetTextVersionAsync();
                Assert.NotEqual(dversion, dversion1); // new document version
                Assert.True(dversion1.TestOnly_IsNewerThan(dversion));
                Assert.Equal(solution.Version, solution1.Version); // updating document should not have changed solution version
                Assert.Equal(project.Version, document1.Project.Version); // updating doc should not have changed project version
                var latestDV1 = await document1.Project.GetLatestDocumentVersionAsync();
                Assert.NotEqual(latestDV, latestDV1);
                Assert.True(latestDV1.TestOnly_IsNewerThan(latestDV));
                Assert.Equal(latestDV1, await document1.GetTextVersionAsync()); // projects latest doc version should be this doc's version

                // update project
                var solution2 = solution1.WithProjectCompilationOptions(project.Id, project.CompilationOptions.WithOutputKind(OutputKind.NetModule));
                var document2 = solution2.GetDocument(document.Id);
                var dversion2 = await document2.GetTextVersionAsync();
                Assert.Equal(dversion1, dversion2); // document didn't change, so version should be the same.
                Assert.NotEqual(document1.Project.Version, document2.Project.Version); // project did change, so project versions should be different
                Assert.True(document2.Project.Version.TestOnly_IsNewerThan(document1.Project.Version));
                Assert.Equal(solution1.Version, solution2.Version); // solution didn't change, just individual project.

                // update solution
                var pid2 = ProjectId.CreateNewId();
                var solution3 = solution2.AddProject(pid2, "foo", "foo", LanguageNames.CSharp);
                Assert.NotEqual(solution2.Version, solution3.Version); // solution changed, added project.
                Assert.True(solution3.Version.TestOnly_IsNewerThan(solution2.Version));
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenSolution_LoadMetadataForReferencedProjects()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var projectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                workspace.LoadMetadataForReferencedProjects = true;

                var project = await workspace.OpenProjectAsync(projectFilePath);
                var document = project.Documents.First();
                var tree = await document.GetSyntaxTreeAsync();
                var expectedFileName = GetSolutionFileName(@"CSharpProject\CSharpClass.cs");
                Assert.Equal(expectedFileName, tree.FilePath);
                var type = tree.GetRoot().DescendantTokens().First(t => t.ToString() == "class").Parent;
                Assert.NotNull(type);
                Assert.StartsWith("public class CSharpClass", type.ToString(), StringComparison.Ordinal);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(739043, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/739043")]
        public async Task TestOpenProject_CSharp_WithoutPrefer32BitAndConsoleApplication()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", Resources.ProjectFiles.CSharp.WithoutPrefer32Bit));
            var projectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projectFilePath);
                var compilation = await project.GetCompilationAsync();
                Assert.Equal(Platform.AnyCpu, compilation.Options.Platform);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(739043, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/739043")]
        public async Task TestOpenProject_CSharp_WithoutPrefer32BitAndLibrary()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", Resources.ProjectFiles.CSharp.WithoutPrefer32Bit)
                .ReplaceFileElement(@"CSharpProject\CSharpProject.csproj", "OutputType", "Library"));
            var projectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projectFilePath);
                var compilation = await project.GetCompilationAsync();
                Assert.Equal(Platform.AnyCpu, compilation.Options.Platform);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(739043, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/739043")]
        public async Task TestOpenProject_CSharp_WithPrefer32BitAndConsoleApplication()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", Resources.ProjectFiles.CSharp.WithPrefer32Bit));
            var projectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projectFilePath);
                var compilation = await project.GetCompilationAsync();
                Assert.Equal(Platform.AnyCpu32BitPreferred, compilation.Options.Platform);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(739043, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/739043")]
        public async Task TestOpenProject_CSharp_WithPrefer32BitAndLibrary()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", Resources.ProjectFiles.CSharp.WithPrefer32Bit)
                .ReplaceFileElement(@"CSharpProject\CSharpProject.csproj", "OutputType", "Library"));
            var projectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projectFilePath);
                var compilation = await project.GetCompilationAsync();
                Assert.Equal(Platform.AnyCpu, compilation.Options.Platform);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(739043, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/739043")]
        public async Task TestOpenProject_CSharp_WithPrefer32BitAndWinMDObj()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", Resources.ProjectFiles.CSharp.WithPrefer32Bit)
                .ReplaceFileElement(@"CSharpProject\CSharpProject.csproj", "OutputType", "winmdobj"));
            var projectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projectFilePath);
                var compilation = await project.GetCompilationAsync();
                Assert.Equal(Platform.AnyCpu, compilation.Options.Platform);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_CSharp_WithoutOutputPath()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .ReplaceFileElement(@"CSharpProject\CSharpProject.csproj", "OutputPath", ""));
            var projectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projectFilePath);
                Assert.NotEmpty(project.OutputFilePath);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_CSharp_WithoutAssemblyName()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .ReplaceFileElement(@"CSharpProject\CSharpProject.csproj", "AssemblyName", ""));
            var projectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projectFilePath);
                Assert.NotEmpty(project.OutputFilePath);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenSolution_CSharp_WithoutCSharpTargetsImported_DocumentsArePickedUp()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", Resources.ProjectFiles.CSharp.WithoutCSharpTargetsImported));
            var solutionFilePath = GetSolutionFileName(@"TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.Projects.First();
                Assert.NotEmpty(project.Documents);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_WithXaml()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", Resources.ProjectFiles.CSharp.WithXaml)
                .WithFile(@"CSharpProject\App.xaml", Resources.SourceFiles.Xaml.App)
                .WithFile(@"CSharpProject\App.xaml.cs", Resources.SourceFiles.CSharp.App)
                .WithFile(@"CSharpProject\MainWindow.xaml", Resources.SourceFiles.Xaml.MainWindow)
                .WithFile(@"CSharpProject\MainWindow.xaml.cs", Resources.SourceFiles.CSharp.MainWindow));
            var projectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");

            // Ensure the Xaml compiler does not run in a separate appdomain. It appears that this won't work within xUnit.
            using (var workspace = CreateMSBuildWorkspace(("AlwaysCompileMarkupFilesInSeparateDomain", "false")))
            {
                var project = await workspace.OpenProjectAsync(projectFilePath);
                var documents = project.Documents.ToList();

                // AssemblyInfo.cs, App.xaml.cs, MainWindow.xaml.cs, App.g.cs, MainWindow.g.cs, + unusual AssemblyAttributes.cs
                Assert.Equal(6, documents.Count);

                // both xaml code behind files are documents
                Assert.Contains(documents, d => d.Name == "App.xaml.cs");
                Assert.Contains(documents, d => d.Name == "MainWindow.xaml.cs");

                // prove no xaml files are documents
                Assert.DoesNotContain(documents, d => d.Name.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase));

                // prove that generated source files for xaml files are included in documents list
                Assert.Contains(documents, d => d.Name == "App.g.cs");
                Assert.Contains(documents, d => d.Name == "MainWindow.g.cs");
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestMetadataReferenceHasBadHintPath()
        {
            // prove that even with bad hint path for metadata reference the workspace can succeed at finding the correct metadata reference.
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", Resources.ProjectFiles.CSharp.BadHintPath));
            var solutionFilePath = GetSolutionFileName(@"TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.Projects.First();
                var refs = project.MetadataReferences.ToList();
                var csharpLib = refs.OfType<PortableExecutableReference>().FirstOrDefault(r => r.FilePath.Contains("Microsoft.CSharp"));
                Assert.NotNull(csharpLib);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(531631, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/531631")]
        public async Task TestOpenProject_AssemblyNameIsPath()
        {
            // prove that even if assembly name is specified as a path instead of just a name, workspace still succeeds at opening project.
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", Resources.ProjectFiles.CSharp.AssemblyNameIsPath));

            var solutionFilePath = GetSolutionFileName(@"TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.Projects.First();
                var comp = await project.GetCompilationAsync();
                Assert.Equal("ReproApp", comp.AssemblyName);
                var expectedOutputPath = GetParentDirOfParentDirOfContainingDir(project.FilePath);
                Assert.Equal(expectedOutputPath, Path.GetDirectoryName(project.OutputFilePath));
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(531631, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/531631")]
        public async Task TestOpenProject_AssemblyNameIsPath2()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", Resources.ProjectFiles.CSharp.AssemblyNameIsPath2));

            var solutionFilePath = GetSolutionFileName(@"TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.Projects.First();
                var comp = await project.GetCompilationAsync();
                Assert.Equal("ReproApp", comp.AssemblyName);
                var expectedOutputPath = Path.Combine(Path.GetDirectoryName(project.FilePath), @"bin");
                Assert.Equal(expectedOutputPath, Path.GetDirectoryName(Path.GetFullPath(project.OutputFilePath)));
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_WithDuplicateFile()
        {
            // Verify that we don't throw in this case
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", Resources.ProjectFiles.CSharp.DuplicateFile));

            var solutionFilePath = GetSolutionFileName(@"TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.Projects.First();
                var documents = project.Documents.Where(d => d.Name == "CSharpClass.cs").ToList();
                Assert.Equal(2, documents.Count);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public void TestOpenProject_WithInvalidFileExtension()
        {
            // make sure the file does in fact exist, but with an unrecognized extension
            const string projFileName = @"CSharpProject\CSharpProject.csproj.nyi";
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(projFileName, Resources.ProjectFiles.CSharp.CSharpProject));

            AssertEx.Throws<InvalidOperationException>(delegate
            {
                MSBuildWorkspace.Create().OpenProjectAsync(GetSolutionFileName(projFileName)).Wait();
            },
            (e) =>
            {
                var expected = string.Format(WorkspacesResources.Cannot_open_project_0_because_the_file_extension_1_is_not_associated_with_a_language, GetSolutionFileName(projFileName), ".nyi");
                Assert.Equal(expected, e.Message);
            });
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public void TestOpenProject_ProjectFileExtensionAssociatedWithUnknownLanguage()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var projFileName = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");
            var language = "lingo";
            AssertEx.Throws<InvalidOperationException>(delegate
            {
                var ws = MSBuildWorkspace.Create();
                ws.AssociateFileExtensionWithLanguage("csproj", language); // non-existent language
                ws.OpenProjectAsync(projFileName).Wait();
            },
            (e) =>
            {
                // the exception should tell us something about the language being unrecognized.
                var expected = string.Format(WorkspacesResources.Cannot_open_project_0_because_the_language_1_is_not_supported, projFileName, language);
                Assert.Equal(expected, e.Message);
            });
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_WithAssociatedLanguageExtension1()
        {
            // make a CSharp solution with a project file having the incorrect extension 'vbproj', and then load it using the overload the lets us
            // specify the language directly, instead of inferring from the extension
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.vbproj", Resources.ProjectFiles.CSharp.CSharpProject));
            var projectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.vbproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                workspace.AssociateFileExtensionWithLanguage("vbproj", LanguageNames.CSharp);
                var project = await workspace.OpenProjectAsync(projectFilePath);
                var document = project.Documents.First();
                var tree = await document.GetSyntaxTreeAsync();
                var diagnostics = tree.GetDiagnostics();
                Assert.Empty(diagnostics);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_WithAssociatedLanguageExtension2_IgnoreCase()
        {
            // make a CSharp solution with a project file having the incorrect extension 'anyproj', and then load it using the overload the lets us
            // specify the language directly, instead of inferring from the extension
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.anyproj", Resources.ProjectFiles.CSharp.CSharpProject));
            var projectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.anyproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                // prove that the association works even if the case is different
                workspace.AssociateFileExtensionWithLanguage("ANYPROJ", LanguageNames.CSharp);
                var project = await workspace.OpenProjectAsync(projectFilePath);
                var document = project.Documents.First();
                var tree = await document.GetSyntaxTreeAsync();
                var diagnostics = tree.GetDiagnostics();
                Assert.Empty(diagnostics);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public void TestOpenSolution_WithNonExistentSolutionFile_Fails()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var solutionFilePath = GetSolutionFileName("NonExistentSolution.sln");

            AssertEx.Throws<FileNotFoundException>(() =>
            {
                using (var workspace = CreateMSBuildWorkspace())
                {
                    workspace.OpenSolutionAsync(solutionFilePath).Wait();
                }
            });
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public void TestOpenSolution_WithInvalidSolutionFile_Fails()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var solutionFilePath = GetSolutionFileName(@"http://localhost/Invalid/InvalidSolution.sln");

            AssertEx.Throws<InvalidOperationException>(() =>
            {
                using (var workspace = CreateMSBuildWorkspace())
                {
                    workspace.OpenSolutionAsync(solutionFilePath).Wait();
                }
            });
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenSolution_WithTemporaryLockedFile_SucceedsWithoutFailureEvent()
        {
            // when skipped we should see a diagnostic for the invalid project

            CreateFiles(GetSimpleCSharpSolutionFiles());

            using (var ws = CreateMSBuildWorkspace())
            {
                var failed = false;
                ws.WorkspaceFailed += (s, args) =>
                {
                    failed |= args.Diagnostic is DocumentDiagnostic;
                };

                // open source file so it cannot be read by workspace;
                var sourceFile = GetSolutionFileName(@"CSharpProject\CSharpClass.cs");
                var file = File.Open(sourceFile, FileMode.Open, FileAccess.Write, FileShare.None);
                try
                {
                    var solution = await ws.OpenSolutionAsync(GetSolutionFileName(@"TestSolution.sln"));
                    var doc = solution.Projects.First().Documents.First(d => d.FilePath == sourceFile);

                    // start reading text
                    var getTextTask = doc.GetTextAsync();

                    // wait 1 unit of retry delay then close file
                    var delay = TextDocumentState.RetryDelay;
                    await Task.Delay(delay).ContinueWith(t => file.Close(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

                    // finish reading text
                    var text = await getTextTask;
                    Assert.NotEmpty(text.ToString());
                }
                finally
                {
                    file.Close();
                }

                Assert.False(failed);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenSolution_WithLockedFile_FailsWithFailureEvent()
        {
            // when skipped we should see a diagnostic for the invalid project

            CreateFiles(GetSimpleCSharpSolutionFiles());
            var solutionFilePath = GetSolutionFileName(@"TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var failed = false;
                workspace.WorkspaceFailed += (s, args) =>
                {
                    failed |= args.Diagnostic is DocumentDiagnostic;
                };

                // open source file so it cannot be read by workspace;
                var sourceFile = GetSolutionFileName(@"CSharpProject\CSharpClass.cs");
                var file = File.Open(sourceFile, FileMode.Open, FileAccess.Write, FileShare.None);
                try
                {
                    var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                    var doc = solution.Projects.First().Documents.First(d => d.FilePath == sourceFile);
                    var text = await doc.GetTextAsync();
                    Assert.Empty(text.ToString());
                }
                finally
                {
                    file.Close();
                }

                Assert.True(failed);
            }
        }


        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenSolution_WithInvalidProjectPath_SkipTrue_SucceedsWithFailureEvent()
        {
            // when skipped we should see a diagnostic for the invalid project

            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"TestSolution.sln", Resources.SolutionFiles.InvalidProjectPath));

            var solutionFilePath = GetSolutionFileName(@"TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var diagnostics = new List<WorkspaceDiagnostic>();
                workspace.WorkspaceFailed += (s, args) =>
                {
                    diagnostics.Add(args.Diagnostic);
                };

                var solution = await workspace.OpenSolutionAsync(solutionFilePath);

                Assert.Single(diagnostics);
            }
        }

        [WorkItem(985906, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/985906")]
        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task HandleSolutionProjectTypeSolutionFolder()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"TestSolution.sln", Resources.SolutionFiles.SolutionFolder));
            var solutionFilePath = GetSolutionFileName(@"TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var diagnostics = new List<WorkspaceDiagnostic>();
                workspace.WorkspaceFailed += (s, args) =>
                {
                    diagnostics.Add(args.Diagnostic);
                };

                var solution = await workspace.OpenSolutionAsync(solutionFilePath);

                Assert.Empty(diagnostics);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public void TestOpenSolution_WithInvalidProjectPath_SkipFalse_Fails()
        {
            // when not skipped we should get an exception for the invalid project

            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"TestSolution.sln", Resources.SolutionFiles.InvalidProjectPath));
            var solutionFilePath = GetSolutionFileName(@"TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                workspace.SkipUnrecognizedProjects = false;

                AssertEx.Throws<InvalidOperationException>(() =>
                {
                    workspace.OpenSolutionAsync(solutionFilePath).Wait();
                });
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenSolution_WithNonExistentProject_SkipTrue_SucceedsWithFailureEvent()
        {
            // when skipped we should see a diagnostic for the non-existent project

            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"TestSolution.sln", Resources.SolutionFiles.NonExistentProject));
            var solutionFilePath = GetSolutionFileName(@"TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var diagnostics = new List<WorkspaceDiagnostic>();
                workspace.WorkspaceFailed += (s, args) =>
                {
                    diagnostics.Add(args.Diagnostic);
                };

                var solution = await workspace.OpenSolutionAsync(solutionFilePath);

                Assert.Single(diagnostics);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public void TestOpenSolution_WithNonExistentProject_SkipFalse_Fails()
        {
            // when skipped we should see an exception for the non-existent project

            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"TestSolution.sln", Resources.SolutionFiles.NonExistentProject));
            var solutionFilePath = GetSolutionFileName(@"TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                workspace.SkipUnrecognizedProjects = false;

                AssertEx.Throws<FileNotFoundException>(() =>
                {
                    workspace.OpenSolutionAsync(solutionFilePath).Wait();
                });
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenSolution_WithUnrecognizedProjectFileExtension_Fails()
        {
            // proves that for solution open, project type guid and extension are both necessary
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"TestSolution.sln", Resources.SolutionFiles.CSharp_UnknownProjectExtension)
                .WithFile(@"CSharpProject\CSharpProject.noproj", Resources.ProjectFiles.CSharp.CSharpProject));
            var solutionFilePath = GetSolutionFileName(@"TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                Assert.Empty(solution.ProjectIds);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenSolution_WithUnrecognizedProjectTypeGuidButRecognizedExtension_Succeeds()
        {
            // proves that if project type guid is not recognized, a known project file extension is all we need.
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"TestSolution.sln", Resources.SolutionFiles.CSharp_UnknownProjectTypeGuid));
            var solutionFilePath = GetSolutionFileName(@"TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                Assert.Single(solution.ProjectIds);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenSolution_WithUnrecognizedProjectTypeGuidAndUnrecognizedExtension_WithSkipTrue_SucceedsWithFailureEvent()
        {
            // proves that if both project type guid and file extension are unrecognized, then project is skipped.
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"TestSolution.sln", Resources.SolutionFiles.CSharp_UnknownProjectTypeGuidAndUnknownExtension)
                .WithFile(@"CSharpProject\CSharpProject.noproj", Resources.ProjectFiles.CSharp.CSharpProject));

            var solutionFilePath = GetSolutionFileName(@"TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var diagnostics = new List<WorkspaceDiagnostic>();
                workspace.WorkspaceFailed += (s, args) =>
                {
                    diagnostics.Add(args.Diagnostic);
                };

                var solution = await workspace.OpenSolutionAsync(solutionFilePath);

                Assert.Single(diagnostics);
                Assert.Empty(solution.ProjectIds);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public void TestOpenSolution_WithUnrecognizedProjectTypeGuidAndUnrecognizedExtension_WithSkipFalse_Fails()
        {
            // proves that if both project type guid and file extension are unrecognized, then open project fails.
            const string noProjFileName = @"CSharpProject\CSharpProject.noproj";
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"TestSolution.sln", Resources.SolutionFiles.CSharp_UnknownProjectTypeGuidAndUnknownExtension)
                .WithFile(noProjFileName, Resources.ProjectFiles.CSharp.CSharpProject));

            var solutionFilePath = GetSolutionFileName(@"TestSolution.sln");

            AssertEx.Throws<InvalidOperationException>(() =>
            {
                using (var workspace = CreateMSBuildWorkspace())
                {
                    workspace.SkipUnrecognizedProjects = false;
                    workspace.OpenSolutionAsync(solutionFilePath).Wait();
                }
            },
            e =>
            {
                var noProjFullFileName = GetSolutionFileName(noProjFileName);
                var expected = string.Format(WorkspacesResources.Cannot_open_project_0_because_the_file_extension_1_is_not_associated_with_a_language, noProjFullFileName, ".noproj");
                Assert.Equal(expected, e.Message);
            });
        }

        private IEnumerable<Assembly> _defaultAssembliesWithoutCSharp = MefHostServices.DefaultAssemblies.Where(a => !a.FullName.Contains("CSharp"));

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(3931, "https://github.com/dotnet/roslyn/issues/3931")]
        public void TestOpenSolution_WithMissingLanguageLibraries_WithSkipFalse_Throws()
        {
            // proves that if the language libraries are missing then the appropriate error occurs
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var solutionFilePath = GetSolutionFileName(@"TestSolution.sln");

            AssertEx.Throws<InvalidOperationException>(() =>
            {
                using (var workspace = CreateMSBuildWorkspace(MefHostServices.Create(_defaultAssembliesWithoutCSharp)))
                {
                    workspace.SkipUnrecognizedProjects = false;
                    workspace.OpenSolutionAsync(solutionFilePath).Wait();
                }
            },
            e =>
            {
                var projFileName = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");
                var expected = string.Format(WorkspacesResources.Cannot_open_project_0_because_the_file_extension_1_is_not_associated_with_a_language, projFileName, ".csproj");
                Assert.Equal(expected, e.Message);
            });
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(3931, "https://github.com/dotnet/roslyn/issues/3931")]
        public async Task TestOpenSolution_WithMissingLanguageLibraries_WithSkipTrue_SucceedsWithDiagnostic()
        {
            // proves that if the language libraries are missing then the appropriate error occurs
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var solutionFilePath = GetSolutionFileName(@"TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace(MefHostServices.Create(_defaultAssembliesWithoutCSharp)))
            {
                workspace.SkipUnrecognizedProjects = true;

                var diagnostics = new List<WorkspaceDiagnostic>();
                workspace.WorkspaceFailed += delegate (object sender, WorkspaceDiagnosticEventArgs e)
                {
                    diagnostics.Add(e.Diagnostic);
                };

                var solution = await workspace.OpenSolutionAsync(solutionFilePath);

                Assert.Single(diagnostics);

                var projFileName = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");
                var expected = string.Format(WorkspacesResources.Cannot_open_project_0_because_the_file_extension_1_is_not_associated_with_a_language, projFileName, ".csproj");
                Assert.Equal(expected, diagnostics[0].Message);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(3931, "https://github.com/dotnet/roslyn/issues/3931")]
        public void TestOpenProject_WithMissingLanguageLibraries_Throws()
        {
            // proves that if the language libraries are missing then the appropriate error occurs
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var projectName = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");

            using (var workspace = MSBuildWorkspace.Create(MefHostServices.Create(_defaultAssembliesWithoutCSharp)))
            {
                AssertEx.Throws<InvalidOperationException>(() =>
                {
                    var project = workspace.OpenProjectAsync(projectName).Result;
                },
                e =>
                {
                    var expected = string.Format(WorkspacesResources.Cannot_open_project_0_because_the_file_extension_1_is_not_associated_with_a_language, projectName, ".csproj");
                    Assert.Equal(expected, e.Message);
                });
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public void TestOpenProject_WithInvalidFilePath_Fails()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var projectFilePath = GetSolutionFileName(@"http://localhost/Invalid/InvalidProject.csproj");

            AssertEx.Throws<InvalidOperationException>(() =>
            {
                using (var workspace = CreateMSBuildWorkspace())
                {
                    workspace.OpenProjectAsync(projectFilePath).Wait();
                }
            });
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public void TestOpenProject_WithNonExistentProjectFile_Fails()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var projectFilePath = GetSolutionFileName(@"CSharpProject\NonExistentProject.csproj");

            AssertEx.Throws<FileNotFoundException>(() =>
            {
                using (var workspace = CreateMSBuildWorkspace())
                {
                    workspace.OpenProjectAsync(projectFilePath).Wait();
                }
            });
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_WithReferencedProject_LoadMetadata_ExistingMetadata_Succeeds()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"CSharpProject\bin\Debug\CSharpProject.dll", Resources.Dlls.CSharpProject));

            var projectFilePath = GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj");

            // keep metadata reference from holding files open
            Workspace.TestHookStandaloneProjectsDoNotHoldReferences = true;

            using (var workspace = CreateMSBuildWorkspace())
            {
                workspace.LoadMetadataForReferencedProjects = true;
                var project = await workspace.OpenProjectAsync(projectFilePath);

                // referenced project got converted to a metadata reference
                var projRefs = project.ProjectReferences.ToList();
                var metaRefs = project.MetadataReferences.ToList();
                Assert.Empty(projRefs);
                Assert.Contains(metaRefs, r => r is PortableExecutableReference && ((PortableExecutableReference)r).Display.Contains("CSharpProject.dll"));
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_WithReferencedProject_LoadMetadata_NonExistentMetadata_LoadsProjectInstead()
        {
            CreateFiles(GetMultiProjectSolutionFiles());
            var projectFilePath = GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj");

            // keep metadata reference from holding files open
            Workspace.TestHookStandaloneProjectsDoNotHoldReferences = true;

            using (var workspace = CreateMSBuildWorkspace())
            {
                workspace.LoadMetadataForReferencedProjects = true;
                var project = await workspace.OpenProjectAsync(projectFilePath);

                // referenced project is still a project ref, did not get converted to metadata ref
                var projRefs = project.ProjectReferences.ToList();
                var metaRefs = project.MetadataReferences.ToList();
                Assert.Single(projRefs);
                Assert.DoesNotContain(metaRefs, r => r.Properties.Aliases.Contains("CSharpProject"));
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_UpdateExistingReferences()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"CSharpProject\bin\Debug\CSharpProject.dll", Resources.Dlls.CSharpProject));
            var vbProjectFilePath = GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj");
            var csProjectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");

            // keep metadata reference from holding files open
            Workspace.TestHookStandaloneProjectsDoNotHoldReferences = true;

            // first open vb project that references c# project, but only reference the c# project's built metadata
            using (var workspace = CreateMSBuildWorkspace())
            {
                workspace.LoadMetadataForReferencedProjects = true;
                var vbProject = await workspace.OpenProjectAsync(vbProjectFilePath);

                // prove vb project references c# project as a metadata reference
                Assert.Empty(vbProject.ProjectReferences);
                Assert.Contains(vbProject.MetadataReferences, r => r is PortableExecutableReference && ((PortableExecutableReference)r).Display.Contains("CSharpProject.dll"));

                // now explicitly open the c# project that got referenced as metadata
                var csProject = await workspace.OpenProjectAsync(csProjectFilePath);

                // show that the vb project now references the c# project directly (not as metadata)
                vbProject = workspace.CurrentSolution.GetProject(vbProject.Id);
                Assert.Single(vbProject.ProjectReferences);
                Assert.DoesNotContain(vbProject.MetadataReferences, r => r.Properties.Aliases.Contains("CSharpProject"));
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_DebugType_Full()
        {
            CreateCSharpFilesWith("DebugType", "full");
            await AssertCSParseOptionsAsync(0, options => options.Errors.Length);
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_DebugType_None()
        {
            CreateCSharpFilesWith("DebugType", "none");
            await AssertCSParseOptionsAsync(0, options => options.Errors.Length);
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_DebugType_PDBOnly()
        {
            CreateCSharpFilesWith("DebugType", "pdbonly");
            await AssertCSParseOptionsAsync(0, options => options.Errors.Length);
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_DebugType_Portable()
        {
            CreateCSharpFilesWith("DebugType", "portable");
            await AssertCSParseOptionsAsync(0, options => options.Errors.Length);
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_DebugType_Embedded()
        {
            CreateCSharpFilesWith("DebugType", "embedded");
            await AssertCSParseOptionsAsync(0, options => options.Errors.Length);
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_OutputKind_DynamicallyLinkedLibrary()
        {
            CreateCSharpFilesWith("OutputType", "Library");
            await AssertCSCompilationOptionsAsync(OutputKind.DynamicallyLinkedLibrary, options => options.OutputKind);
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_OutputKind_ConsoleApplication()
        {
            CreateCSharpFilesWith("OutputType", "Exe");
            await AssertCSCompilationOptionsAsync(OutputKind.ConsoleApplication, options => options.OutputKind);
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_OutputKind_WindowsApplication()
        {
            CreateCSharpFilesWith("OutputType", "WinExe");
            await AssertCSCompilationOptionsAsync(OutputKind.WindowsApplication, options => options.OutputKind);
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_OutputKind_NetModule()
        {
            CreateCSharpFilesWith("OutputType", "Module");
            await AssertCSCompilationOptionsAsync(OutputKind.NetModule, options => options.OutputKind);
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_OptimizationLevel_Release()
        {
            CreateCSharpFilesWith("Optimize", "True");
            await AssertCSCompilationOptionsAsync(OptimizationLevel.Release, options => options.OptimizationLevel);
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_OptimizationLevel_Debug()
        {
            CreateCSharpFilesWith("Optimize", "False");
            await AssertCSCompilationOptionsAsync(OptimizationLevel.Debug, options => options.OptimizationLevel);
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_MainFileName()
        {
            CreateCSharpFilesWith("StartupObject", "Foo");
            await AssertCSCompilationOptionsAsync("Foo", options => options.MainTypeName);
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_AssemblyOriginatorKeyFile_SignAssembly_Missing()
        {
            CreateCSharpFiles();
            await AssertCSCompilationOptionsAsync(null, options => options.CryptoKeyFile);
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_AssemblyOriginatorKeyFile_SignAssembly_False()
        {
            CreateCSharpFilesWith("SignAssembly", "false");
            await AssertCSCompilationOptionsAsync(null, options => options.CryptoKeyFile);
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_AssemblyOriginatorKeyFile_SignAssembly_True()
        {
            CreateCSharpFilesWith("SignAssembly", "true");
            await AssertCSCompilationOptionsAsync("snKey.snk", options => Path.GetFileName(options.CryptoKeyFile));
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_AssemblyOriginatorKeyFile_DelaySign_False()
        {
            CreateCSharpFilesWith("DelaySign", "false");
            await AssertCSCompilationOptionsAsync(null, options => options.DelaySign);
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_AssemblyOriginatorKeyFile_DelaySign_True()
        {
            CreateCSharpFilesWith("DelaySign", "true");
            await AssertCSCompilationOptionsAsync(true, options => options.DelaySign);
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_CheckOverflow_True()
        {
            CreateCSharpFilesWith("CheckForOverflowUnderflow", "true");
            await AssertCSCompilationOptionsAsync(true, options => options.CheckOverflow);
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_CheckOverflow_False()
        {
            CreateCSharpFilesWith("CheckForOverflowUnderflow", "false");
            await AssertCSCompilationOptionsAsync(false, options => options.CheckOverflow);
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestParseOptions_CSharp_Compatibility_ECMA1()
        {
            CreateCSharpFilesWith("LangVersion", "ISO-1");
            await AssertCSParseOptionsAsync(CS.LanguageVersion.CSharp1, options => options.LanguageVersion);
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestParseOptions_CSharp_Compatibility_ECMA2()
        {
            CreateCSharpFilesWith("LangVersion", "ISO-2");
            await AssertCSParseOptionsAsync(CS.LanguageVersion.CSharp2, options => options.LanguageVersion);
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestParseOptions_CSharp_Compatibility_None()
        {
            CreateCSharpFilesWith("LangVersion", "3");
            await AssertCSParseOptionsAsync(CS.LanguageVersion.CSharp3, options => options.LanguageVersion);
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestParseOptions_CSharp_LanguageVersion_Default()
        {
            CreateCSharpFiles();
            await AssertCSParseOptionsAsync(CS.LanguageVersion.CSharp7, options => options.LanguageVersion);
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestParseOptions_CSharp_PreprocessorSymbols()
        {
            CreateCSharpFilesWith("DefineConstants", "DEBUG;TRACE;X;Y");
            await AssertCSParseOptionsAsync("DEBUG,TRACE,X,Y", options => string.Join(",", options.PreprocessorSymbolNames));
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestConfigurationDebug()
        {
            CreateCSharpFiles();
            await AssertCSParseOptionsAsync("DEBUG,TRACE", options => string.Join(",", options.PreprocessorSymbolNames));
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestConfigurationRelease()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace(("Configuration", "Release")))
            {
                var sol = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = sol.Projects.First();
                var options = project.ParseOptions;

                Assert.DoesNotContain(options.PreprocessorSymbolNames, name => name == "DEBUG");
                Assert.Contains(options.PreprocessorSymbolNames, name => name == "TRACE");
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task Test_CSharp_ConditionalAttributeEmitted()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpClass.cs", Resources.SourceFiles.CSharp.CSharpClass_WithConditionalAttributes)
                .ReplaceFileElement(@"CSharpProject\CSharpProject.csproj", "DefineConstants", "EnableMyAttribute"));
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var sol = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = sol.GetProjectsByName("CSharpProject").FirstOrDefault();
                var options = project.ParseOptions;
                Assert.Contains("EnableMyAttribute", options.PreprocessorSymbolNames);

                var compilation = await project.GetCompilationAsync();
                var metadataBytes = compilation.EmitToArray();
                var mtref = MetadataReference.CreateFromImage(metadataBytes);
                var mtcomp = CS.CSharpCompilation.Create("MT", references: new MetadataReference[] { mtref });
                var sym = (IAssemblySymbol)mtcomp.GetAssemblyOrModuleSymbol(mtref);
                var attrs = sym.GetAttributes();

                Assert.Contains(attrs, ad => ad.AttributeClass.Name == "MyAttr");
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task Test_CSharp_ConditionalAttributeNotEmitted()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpClass.cs", Resources.SourceFiles.CSharp.CSharpClass_WithConditionalAttributes));
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var sol = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = sol.GetProjectsByName("CSharpProject").FirstOrDefault();
                var options = project.ParseOptions;
                Assert.DoesNotContain("EnableMyAttribute", options.PreprocessorSymbolNames);

                var compilation = await project.GetCompilationAsync();
                var metadataBytes = compilation.EmitToArray();
                var mtref = MetadataReference.CreateFromImage(metadataBytes);
                var mtcomp = CS.CSharpCompilation.Create("MT", references: new MetadataReference[] { mtref });
                var sym = (IAssemblySymbol)mtcomp.GetAssemblyOrModuleSymbol(mtref);
                var attrs = sym.GetAttributes();

                Assert.DoesNotContain(attrs, ad => ad.AttributeClass.Name == "MyAttr");
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_CSharp_WithLinkedDocument()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", Resources.ProjectFiles.CSharp.WithLink)
                .WithFile(@"OtherStuff\Foo.cs", Resources.SourceFiles.CSharp.OtherStuff_Foo));

            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.GetProjectsByName("CSharpProject").FirstOrDefault();
                var documents = project.Documents.ToList();
                var fooDoc = documents.Single(d => d.Name == "Foo.cs");
                var folder = Assert.Single(fooDoc.Folders);
                Assert.Equal("Blah", folder);

                // prove that the file path is the correct full path to the actual file
                Assert.Contains("OtherStuff", fooDoc.FilePath);
                Assert.True(File.Exists(fooDoc.FilePath));
                var text = File.ReadAllText(fooDoc.FilePath);
                Assert.Equal(Resources.SourceFiles.CSharp.OtherStuff_Foo, text);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestAddDocumentAsync()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.GetProjectsByName("CSharpProject").FirstOrDefault();

                var newText = SourceText.From("public class Bar { }");
                workspace.AddDocument(project.Id, new string[] { "NewFolder" }, "Bar.cs", newText);

                // check workspace current solution
                var solution2 = workspace.CurrentSolution;
                var project2 = solution2.GetProjectsByName("CSharpProject").FirstOrDefault();
                var documents = project2.Documents.ToList();
                Assert.Equal(4, documents.Count);
                var document2 = documents.Single(d => d.Name == "Bar.cs");
                var text2 = await document2.GetTextAsync();
                Assert.Equal(newText.ToString(), text2.ToString());
                Assert.Single(document2.Folders);

                // check actual file on disk...
                var textOnDisk = File.ReadAllText(document2.FilePath);
                Assert.Equal(newText.ToString(), textOnDisk);

                // check project file on disk
                var projectFileText = File.ReadAllText(project2.FilePath);
                Assert.Contains(@"NewFolder\Bar.cs", projectFileText);

                // reload project & solution to prove project file change was good
                using (var workspaceB = CreateMSBuildWorkspace())
                {
                    var solutionB = await workspaceB.OpenSolutionAsync(solutionFilePath);
                    var projectB = workspaceB.CurrentSolution.GetProjectsByName("CSharpProject").FirstOrDefault();
                    var documentsB = projectB.Documents.ToList();
                    Assert.Equal(4, documentsB.Count);
                    var documentB = documentsB.Single(d => d.Name == "Bar.cs");
                    Assert.Single(documentB.Folders);
                }
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestUpdateDocumentAsync()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.GetProjectsByName("CSharpProject").FirstOrDefault();
                var document = project.Documents.Single(d => d.Name == "CSharpClass.cs");
                var originalText = await document.GetTextAsync();

                var newText = SourceText.From("public class Bar { }");
                workspace.TryApplyChanges(solution.WithDocumentText(document.Id, newText, PreservationMode.PreserveIdentity));

                // check workspace current solution
                var solution2 = workspace.CurrentSolution;
                var project2 = solution2.GetProjectsByName("CSharpProject").FirstOrDefault();
                var documents = project2.Documents.ToList();
                Assert.Equal(3, documents.Count);
                var document2 = documents.Single(d => d.Name == "CSharpClass.cs");
                var text2 = await document2.GetTextAsync();
                Assert.Equal(newText.ToString(), text2.ToString());

                // check actual file on disk...
                var textOnDisk = File.ReadAllText(document2.FilePath);
                Assert.Equal(newText.ToString(), textOnDisk);

                // check original text in original solution did not change
                var text = await document.GetTextAsync();

                Assert.Equal(originalText.ToString(), text.ToString());
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestRemoveDocumentAsync()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.GetProjectsByName("CSharpProject").FirstOrDefault();
                var document = project.Documents.Single(d => d.Name == "CSharpClass.cs");
                var originalText = await document.GetTextAsync();

                workspace.RemoveDocument(document.Id);

                // check workspace current solution
                var solution2 = workspace.CurrentSolution;
                var project2 = solution2.GetProjectsByName("CSharpProject").FirstOrDefault();
                Assert.DoesNotContain(project2.Documents, d => d.Name == "CSharpClass.cs");

                // check actual file on disk...
                Assert.False(File.Exists(document.FilePath));

                // check original text in original solution did not change
                var text = await document.GetTextAsync();
                Assert.Equal(originalText.ToString(), text.ToString());
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestApplyChanges_UpdateDocumentText()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var documents = solution.GetProjectsByName("CSharpProject").FirstOrDefault().Documents.ToList();
                var document = documents.Single(d => d.Name.Contains("CSharpClass"));
                var text = await document.GetTextAsync();
                var newText = SourceText.From("using System.Diagnostics;\r\n" + text.ToString());
                var newSolution = solution.WithDocumentText(document.Id, newText);

                workspace.TryApplyChanges(newSolution);

                // check workspace current solution
                var solution2 = workspace.CurrentSolution;
                var document2 = solution2.GetDocument(document.Id);
                var text2 = await document2.GetTextAsync();
                Assert.Equal(newText.ToString(), text2.ToString());

                // check actual file on disk...
                var textOnDisk = File.ReadAllText(document.FilePath);
                Assert.Equal(newText.ToString(), textOnDisk);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestApplyChanges_AddDocument()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.GetProjectsByName("CSharpProject").FirstOrDefault();
                var newDocId = DocumentId.CreateNewId(project.Id);
                var newText = SourceText.From("public class Bar { }");
                var newSolution = solution.AddDocument(newDocId, "Bar.cs", newText);

                workspace.TryApplyChanges(newSolution);

                // check workspace current solution
                var solution2 = workspace.CurrentSolution;
                var document2 = solution2.GetDocument(newDocId);
                var text2 = await document2.GetTextAsync();
                Assert.Equal(newText.ToString(), text2.ToString());

                // check actual file on disk...
                var textOnDisk = File.ReadAllText(document2.FilePath);
                Assert.Equal(newText.ToString(), textOnDisk);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestApplyChanges_NotSupportedChangesFail()
        {
            var csharpProjPath = @"AnalyzerSolution\CSharpProject_AnalyzerReference.csproj";
            var vbProjPath = @"AnalyzerSolution\VisualBasicProject_AnalyzerReference.vbproj";
            CreateFiles(GetAnalyzerReferenceSolutionFiles());

            using (var workspace = CreateMSBuildWorkspace())
            {
                var csProjectFilePath = GetSolutionFileName(csharpProjPath);
                var csProject = await workspace.OpenProjectAsync(csProjectFilePath);
                var csProjectId = csProject.Id;

                var vbProjectFilePath = GetSolutionFileName(vbProjPath);
                var vbProject = await workspace.OpenProjectAsync(vbProjectFilePath);
                var vbProjectId = vbProject.Id;

                // adding additional documents not supported.
                Assert.False(workspace.CanApplyChange(ApplyChangesKind.AddAdditionalDocument));
                Assert.Throws<NotSupportedException>(delegate
                {
                    workspace.TryApplyChanges(workspace.CurrentSolution.AddAdditionalDocument(DocumentId.CreateNewId(csProjectId), "foo.xaml", SourceText.From("<foo></foo>")));
                });

                var xaml = workspace.CurrentSolution.GetProject(csProjectId).AdditionalDocuments.FirstOrDefault(d => d.Name == "XamlFile.xaml");
                Assert.NotNull(xaml);

                // removing additional documents not supported
                Assert.False(workspace.CanApplyChange(ApplyChangesKind.RemoveAdditionalDocument));
                Assert.Throws<NotSupportedException>(delegate
                {
                    workspace.TryApplyChanges(workspace.CurrentSolution.RemoveAdditionalDocument(xaml.Id));
                });
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestWorkspaceChangedEvent()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                await workspace.OpenSolutionAsync(solutionFilePath);
                var expectedEventKind = WorkspaceChangeKind.DocumentChanged;
                var originalSolution = workspace.CurrentSolution;

                using (var eventWaiter = workspace.VerifyWorkspaceChangedEvent(args =>
                {
                    Assert.Equal(expectedEventKind, args.Kind);
                    Assert.NotNull(args.NewSolution);
                    Assert.NotSame(originalSolution, args.NewSolution);
                }))
                {
                    // change document text (should fire SolutionChanged event)
                    var doc = workspace.CurrentSolution.Projects.First().Documents.First();
                    var text = await doc.GetTextAsync();
                    var newText = "/* new text */\r\n" + text.ToString();

                    workspace.TryApplyChanges(workspace.CurrentSolution.WithDocumentText(doc.Id, SourceText.From(newText), PreservationMode.PreserveIdentity));

                    Assert.True(eventWaiter.WaitForEventToFire(AsyncEventTimeout),
                        string.Format("event {0} was not fired within {1}",
                        Enum.GetName(typeof(WorkspaceChangeKind), expectedEventKind),
                        AsyncEventTimeout));
                }
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestWorkspaceChangedWeakEvent()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                await workspace.OpenSolutionAsync(solutionFilePath);
                var expectedEventKind = WorkspaceChangeKind.DocumentChanged;
                var originalSolution = workspace.CurrentSolution;

                using (var eventWanter = workspace.VerifyWorkspaceChangedEvent(args =>
                {
                    Assert.Equal(expectedEventKind, args.Kind);
                    Assert.NotNull(args.NewSolution);
                    Assert.NotSame(originalSolution, args.NewSolution);
                }))
                {
                    // change document text (should fire SolutionChanged event)
                    var doc = workspace.CurrentSolution.Projects.First().Documents.First();
                    var text = await doc.GetTextAsync();
                    var newText = "/* new text */\r\n" + text.ToString();

                    workspace.TryApplyChanges(
                        workspace
                        .CurrentSolution
                        .WithDocumentText(
                            doc.Id,
                            SourceText.From(newText),
                            PreservationMode.PreserveIdentity));

                    Assert.True(eventWanter.WaitForEventToFire(AsyncEventTimeout),
                        string.Format("event {0} was not fired within {1}",
                        Enum.GetName(typeof(WorkspaceChangeKind), expectedEventKind),
                        AsyncEventTimeout));
                }
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestSemanticVersionCS()
        {
            CreateFiles(GetMultiProjectSolutionFiles());
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);

                var csprojectId = solution.Projects.First(p => p.Language == LanguageNames.CSharp).Id;
                var csdoc1 = solution.GetProject(csprojectId).Documents.Single(d => d.Name == "CSharpClass.cs");

                // add method
                var csdoc1Root = await csdoc1.GetSyntaxRootAsync();
                var startOfClassInterior = csdoc1Root.DescendantNodes().OfType<CS.Syntax.ClassDeclarationSyntax>().First().OpenBraceToken.FullSpan.End;
                var csdoc1Text = await csdoc1.GetTextAsync();
                var csdoc2 = AssertSemanticVersionChanged(csdoc1, csdoc1Text.Replace(new TextSpan(startOfClassInterior, 0), "public async Task M() {\r\n}\r\n"));

                // change interior of method
                var csdoc2Root = await csdoc2.GetSyntaxRootAsync();
                var startOfMethodInterior = csdoc2Root.DescendantNodes().OfType<CS.Syntax.MethodDeclarationSyntax>().First().Body.OpenBraceToken.FullSpan.End;
                var csdoc2Text = await csdoc2.GetTextAsync();
                var csdoc3 = AssertSemanticVersionUnchanged(csdoc2, csdoc2Text.Replace(new TextSpan(startOfMethodInterior, 0), "int x = 10;\r\n"));

                // add whitespace outside of method
                var csdoc3Text = await csdoc3.GetTextAsync();
                var csdoc4 = AssertSemanticVersionUnchanged(csdoc3, csdoc3Text.Replace(new TextSpan(startOfClassInterior, 0), "\r\n\r\n   \r\n"));

                // add field with initializer
                csdoc1Text = await csdoc1.GetTextAsync();
                var csdoc5 = AssertSemanticVersionChanged(csdoc1, csdoc1Text.Replace(new TextSpan(startOfClassInterior, 0), "\r\npublic int X = 20;\r\n"));

                // change initializer value
                var csdoc5Root = await csdoc5.GetSyntaxRootAsync();
                var literal = csdoc5Root.DescendantNodes().OfType<CS.Syntax.LiteralExpressionSyntax>().First(x => x.Token.ValueText == "20");
                var csdoc5Text = await csdoc5.GetTextAsync();
                var csdoc6 = AssertSemanticVersionUnchanged(csdoc5, csdoc5Text.Replace(literal.Span, "100"));

                // add const field with initializer
                csdoc1Text = await csdoc1.GetTextAsync();
                var csdoc7 = AssertSemanticVersionChanged(csdoc1, csdoc1Text.Replace(new TextSpan(startOfClassInterior, 0), "\r\npublic const int X = 20;\r\n"));

                // change constant initializer value
                var csdoc7Root = await csdoc7.GetSyntaxRootAsync();
                var literal7 = csdoc7Root.DescendantNodes().OfType<CS.Syntax.LiteralExpressionSyntax>().First(x => x.Token.ValueText == "20");
                var csdoc7Text = await csdoc7.GetTextAsync();
                var csdoc8 = AssertSemanticVersionChanged(csdoc7, csdoc7Text.Replace(literal7.Span, "100"));
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(529276, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/529276"), WorkItem(12086, "DevDiv_Projects/Roslyn")]
        public async Task TestOpenProject_LoadMetadataForReferenceProjects_NoMetadata()
        {
            var projPath = @"CSharpProject\CSharpProject_ProjectReference.csproj";
            var files = GetProjectReferenceSolutionFiles();

            CreateFiles(files);

            var projectFullPath = GetSolutionFileName(projPath);

            using (var workspace = CreateMSBuildWorkspace())
            {
                workspace.LoadMetadataForReferencedProjects = true;

                var proj = await workspace.OpenProjectAsync(projectFullPath);

                // prove that project gets opened instead.
                Assert.Equal(2, workspace.CurrentSolution.Projects.Count());

                // and all is well
                var comp = await proj.GetCompilationAsync();
                var errs = comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error);
                Assert.Empty(errs);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(918072, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/918072")]
        public async Task TestAnalyzerReferenceLoadStandalone()
        {
            var projPaths = new[] { @"AnalyzerSolution\CSharpProject_AnalyzerReference.csproj", @"AnalyzerSolution\VisualBasicProject_AnalyzerReference.vbproj" };
            var files = GetAnalyzerReferenceSolutionFiles();

            CreateFiles(files);

            using (var workspace = CreateMSBuildWorkspace())
            {
                foreach (var projectPath in projPaths)
                {
                    var projectFullPath = GetSolutionFileName(projectPath);

                    var proj = await workspace.OpenProjectAsync(projectFullPath);
                    Assert.Equal(1, proj.AnalyzerReferences.Count);
                    var analyzerReference = proj.AnalyzerReferences.First() as AnalyzerFileReference;
                    Assert.NotNull(analyzerReference);
                    Assert.True(analyzerReference.FullPath.EndsWith("CSharpProject.dll", StringComparison.OrdinalIgnoreCase));
                }

                // prove that project gets opened instead.
                Assert.Equal(2, workspace.CurrentSolution.Projects.Count());
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestAdditionalFilesStandalone()
        {
            var projPaths = new[] { @"AnalyzerSolution\CSharpProject_AnalyzerReference.csproj", @"AnalyzerSolution\VisualBasicProject_AnalyzerReference.vbproj" };
            var files = GetAnalyzerReferenceSolutionFiles();

            CreateFiles(files);

            using (var workspace = CreateMSBuildWorkspace())
            {
                foreach (var projectPath in projPaths)
                {
                    var projectFullPath = GetSolutionFileName(projectPath);

                    var proj = await workspace.OpenProjectAsync(projectFullPath);
                    var doc = Assert.Single(proj.AdditionalDocuments);
                    Assert.Equal("XamlFile.xaml", doc.Name);
                    var text = await doc.GetTextAsync();
                    Assert.Contains("Window", text.ToString(), StringComparison.Ordinal);
                }
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestLoadTextSync()
        {
            var files = GetAnalyzerReferenceSolutionFiles();

            CreateFiles(files);

            using (var workspace = new AdhocWorkspace(MSBuildMefHostServices.DefaultServices, WorkspaceKind.MSBuild))
            {
                var projectFullPath = GetSolutionFileName(@"AnalyzerSolution\CSharpProject_AnalyzerReference.csproj");

                var loader = new MSBuildProjectLoader(workspace);
                var infos = await loader.LoadProjectInfoAsync(projectFullPath);

                var doc = infos[0].Documents[0];
                var tav = doc.TextLoader.LoadTextAndVersionSynchronously(workspace, doc.Id, CancellationToken.None);

                var adoc = infos[0].AdditionalDocuments.First(a => a.Name == "XamlFile.xaml");
                var atav = adoc.TextLoader.LoadTextAndVersionSynchronously(workspace, adoc.Id, CancellationToken.None);
                Assert.Contains("Window", atav.Text.ToString(), StringComparison.Ordinal);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestGetTextSynchronously()
        {
            var files = GetAnalyzerReferenceSolutionFiles();

            CreateFiles(files);

            using (var workspace = CreateMSBuildWorkspace())
            {
                var projectFullPath = GetSolutionFileName(@"AnalyzerSolution\CSharpProject_AnalyzerReference.csproj");
                var proj = await workspace.OpenProjectAsync(projectFullPath);

                var doc = proj.Documents.First();
                var text = doc.State.GetTextSynchronously(CancellationToken.None);

                var adoc = proj.AdditionalDocuments.First(a => a.Name == "XamlFile.xaml");
                var atext = adoc.State.GetTextSynchronously(CancellationToken.None);
                Assert.Contains("Window", atext.ToString(), StringComparison.Ordinal);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(546171, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/546171")]
        public async Task TestCSharpExternAlias()
        {
            var projPath = @"CSharpProject\CSharpProject_ExternAlias.csproj";
            var files = new FileSet(
                (projPath, Resources.ProjectFiles.CSharp.ExternAlias),
                (@"CSharpProject\CSharpExternAlias.cs", Resources.SourceFiles.CSharp.CSharpExternAlias));

            CreateFiles(files);

            var fullPath = GetSolutionFileName(projPath);
            using (var workspace = CreateMSBuildWorkspace())
            {
                var proj = await workspace.OpenProjectAsync(fullPath);
                var comp = await proj.GetCompilationAsync();
                comp.GetDiagnostics().Where(d => d.Severity > DiagnosticSeverity.Info).Verify();
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(530337, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/530337")]
        public async Task TestProjectReferenceWithExternAlias()
        {
            var files = GetProjectReferenceSolutionFiles();
            CreateFiles(files);

            var fullPath = GetSolutionFileName(@"CSharpProjectReference.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var sol = await workspace.OpenSolutionAsync(fullPath);
                var proj = sol.Projects.First();
                var comp = await proj.GetCompilationAsync();
                comp.GetDiagnostics().Where(d => d.Severity > DiagnosticSeverity.Info).Verify();
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestProjectReferenceWithReferenceOutputAssemblyFalse()
        {
            var files = GetProjectReferenceSolutionFiles();
            files = VisitProjectReferences(files, r =>
            {
                r.Add(new XElement(XName.Get("ReferenceOutputAssembly", MSBuildNamespace), "false"));
            });

            CreateFiles(files);

            var fullPath = GetSolutionFileName(@"CSharpProjectReference.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var sol = await workspace.OpenSolutionAsync(fullPath);
                foreach (var project in sol.Projects)
                {
                    Assert.Empty(project.ProjectReferences);
                }
            }
        }

        private FileSet VisitProjectReferences(FileSet files, Action<XElement> visitProjectReference)
        {
            var result = new List<(string, object)>();
            foreach (var (fileName, fileContent) in files)
            {
                var text = fileContent.ToString();
                if (fileName.EndsWith("proj", StringComparison.OrdinalIgnoreCase))
                {
                    text = VisitProjectReferences(text, visitProjectReference);
                }

                result.Add((fileName, text));
            }

            return new FileSet(result.ToArray());
        }

        private string VisitProjectReferences(string projectFileText, Action<XElement> visitProjectReference)
        {
            var document = XDocument.Parse(projectFileText);
            var projectReferenceItems = document.Descendants(XName.Get("ProjectReference", MSBuildNamespace));
            foreach (var projectReferenceItem in projectReferenceItems)
            {
                visitProjectReference(projectReferenceItem);
            }

            return document.ToString();
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestProjectReferenceWithNoGuid()
        {
            var files = GetProjectReferenceSolutionFiles();
            files = VisitProjectReferences(files, r =>
            {
                r.Elements(XName.Get("Project", MSBuildNamespace)).Remove();
            });

            CreateFiles(files);

            var fullPath = GetSolutionFileName(@"CSharpProjectReference.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var sol = await workspace.OpenSolutionAsync(fullPath);
                foreach (var project in sol.Projects)
                {
                    Assert.InRange(project.ProjectReferences.Count(), 0, 1);
                }
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled), AlwaysSkip = "https://github.com/dotnet/roslyn/issues/23685"), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(5668, "https://github.com/dotnet/roslyn/issues/5668")]
        public async Task TestOpenProject_MetadataReferenceHasDocComments()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.Projects.First();
                var comp = await project.GetCompilationAsync();
                var symbol = comp.GetTypeByMetadataName("System.Console");
                var docComment = symbol.GetDocumentationCommentXml();
                Assert.NotNull(docComment);
                Assert.NotEmpty(docComment);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_CSharp_HasSourceDocComments()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.Projects.First();
                var parseOptions = (CS.CSharpParseOptions)project.ParseOptions;
                Assert.Equal(DocumentationMode.Parse, parseOptions.DocumentationMode);
                var comp = await project.GetCompilationAsync();
                var symbol = comp.GetTypeByMetadataName("CSharpProject.CSharpClass");
                var docComment = symbol.GetDocumentationCommentXml();
                Assert.NotNull(docComment);
                Assert.NotEmpty(docComment);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public void TestOpenProject_WithProjectFileLocked()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());

            // open for read-write so no-one else can read
            var projectFile = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");
            using (File.Open(projectFile, FileMode.Open, FileAccess.ReadWrite))
            {
                AssertEx.Throws<IOException>(() =>
                    {
                        using (var workspace = CreateMSBuildWorkspace())
                        {
                            workspace.OpenProjectAsync(projectFile).Wait();
                        }
                    });
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public void TestOpenProject_WithNonExistentProjectFile()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());

            // open for read-write so no-one else can read
            var projectFile = GetSolutionFileName(@"CSharpProject\NoProject.csproj");
            AssertEx.Throws<FileNotFoundException>(() =>
                {
                    using (var workspace = CreateMSBuildWorkspace())
                    {
                        workspace.OpenProjectAsync(projectFile).Wait();
                    }
                });
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public void TestOpenSolution_WithNonExistentSolutionFile()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());

            // open for read-write so no-one else can read
            var solutionFile = GetSolutionFileName(@"NoSolution.sln");
            AssertEx.Throws<FileNotFoundException>(() =>
                {
                    using (var workspace = CreateMSBuildWorkspace())
                    {
                        workspace.OpenSolutionAsync(solutionFile).Wait();
                    }
                });
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenSolution_SolutionFileHasEmptyLinesAndWhitespaceOnlyLines()
        {
            var files = new FileSet(
                (@"TestSolution.sln", Resources.SolutionFiles.CSharp_EmptyLines),
                (@"CSharpProject\CSharpProject.csproj", Resources.ProjectFiles.CSharp.CSharpProject),
                (@"CSharpProject\CSharpClass.cs", Resources.SourceFiles.CSharp.CSharpClass),
                (@"CSharpProject\Properties\AssemblyInfo.cs", Resources.SourceFiles.CSharp.AssemblyInfo));

            CreateFiles(files);
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.Projects.First();
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(531543, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/531543")]
        public async Task TestOpenSolution_SolutionFileHasEmptyLineBetweenProjectBlock()
        {
            var files = new FileSet(
                (@"TestSolution.sln", Resources.SolutionFiles.EmptyLineBetweenProjectBlock));

            CreateFiles(files);
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled), AlwaysSkip = "MSBuild parsing API throws InvalidProjectFileException")]
        [Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(531283, "DevDiv")]
        public async Task TestOpenSolution_SolutionFileHasMissingEndProject()
        {
            var files = new FileSet(
                (@"TestSolution1.sln", Resources.SolutionFiles.MissingEndProject1),
                (@"TestSolution2.sln", Resources.SolutionFiles.MissingEndProject2),
                (@"TestSolution3.sln", Resources.SolutionFiles.MissingEndProject3));

            CreateFiles(files);

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solutionFilePath = GetSolutionFileName("TestSolution1.sln");
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
            }

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solutionFilePath = GetSolutionFileName("TestSolution2.sln");
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
            }

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solutionFilePath = GetSolutionFileName("TestSolution3.sln");
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(792912, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/792912")]
        public async Task TestOpenSolution_WithDuplicatedGuidsBecomeSelfReferential()
        {
            var files = new FileSet(
                (@"DuplicatedGuids.sln", Resources.SolutionFiles.DuplicatedGuidsBecomeSelfReferential),
                (@"ReferenceTest\ReferenceTest.csproj", Resources.ProjectFiles.CSharp.DuplicatedGuidsBecomeSelfReferential),
                (@"Library1\Library1.csproj", Resources.ProjectFiles.CSharp.DuplicatedGuidLibrary1));

            CreateFiles(files);
            var solutionFilePath = GetSolutionFileName("DuplicatedGuids.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                Assert.Equal(2, solution.ProjectIds.Count);

                var testProject = solution.Projects.FirstOrDefault(p => p.Name == "ReferenceTest");
                Assert.NotNull(testProject);
                Assert.Single(testProject.AllProjectReferences);

                var libraryProject = solution.Projects.FirstOrDefault(p => p.Name == "Library1");
                Assert.NotNull(libraryProject);
                Assert.Empty(libraryProject.AllProjectReferences);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(792912, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/792912")]
        public async Task TestOpenSolution_WithDuplicatedGuidsBecomeCircularReferential()
        {
            var files = new FileSet(
                (@"DuplicatedGuids.sln", Resources.SolutionFiles.DuplicatedGuidsBecomeCircularReferential),
                (@"ReferenceTest\ReferenceTest.csproj", Resources.ProjectFiles.CSharp.DuplicatedGuidsBecomeCircularReferential),
                (@"Library1\Library1.csproj", Resources.ProjectFiles.CSharp.DuplicatedGuidLibrary3),
                (@"Library2\Library2.csproj", Resources.ProjectFiles.CSharp.DuplicatedGuidLibrary4));

            CreateFiles(files);
            var solutionFilePath = GetSolutionFileName("DuplicatedGuids.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                Assert.Equal(3, solution.ProjectIds.Count);

                var testProject = solution.Projects.FirstOrDefault(p => p.Name == "ReferenceTest");
                Assert.NotNull(testProject);
                Assert.Single(testProject.AllProjectReferences);

                var library1Project = solution.Projects.FirstOrDefault(p => p.Name == "Library1");
                Assert.NotNull(library1Project);
                Assert.Single(library1Project.AllProjectReferences);

                var library2Project = solution.Projects.FirstOrDefault(p => p.Name == "Library2");
                Assert.NotNull(library2Project);
                Assert.Empty(library2Project.AllProjectReferences);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_CSharp_WithMissingDebugType()
        {
            CreateFiles(new FileSet(
                (@"ProjectLoadErrorOnMissingDebugType.sln", Resources.SolutionFiles.ProjectLoadErrorOnMissingDebugType),
                (@"ProjectLoadErrorOnMissingDebugType\ProjectLoadErrorOnMissingDebugType.csproj", Resources.ProjectFiles.CSharp.ProjectLoadErrorOnMissingDebugType)));
            var solutionFilePath = GetSolutionFileName(@"ProjectLoadErrorOnMissingDebugType.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                await workspace.OpenSolutionAsync(solutionFilePath);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(991528, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/991528")]
        public async Task MSBuildProjectShouldHandleCodePageProperty()
        {
            var files = new FileSet(
                ("Encoding.csproj", Resources.ProjectFiles.CSharp.Encoding.Replace("<CodePage>ReplaceMe</CodePage>", "<CodePage>1254</CodePage>")),
                ("class1.cs", "//\u201C"));

            CreateFiles(files);

            var projPath = GetSolutionFileName("Encoding.csproj");
            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projPath);
                var document = project.Documents.First(d => d.Name == "class1.cs");
                var text = await document.GetTextAsync();
                Assert.Equal(Encoding.GetEncoding(1254), text.Encoding);

                // The smart quote (“) in class1.cs shows up as "â€œ" in codepage 1254. Do a sanity
                // check here to make sure this file hasn't been corrupted in a way that would
                // impact subsequent asserts.
                Assert.Equal("//\u00E2\u20AC\u0153".Length, 5);
                Assert.Equal("//\u00E2\u20AC\u0153".Length, text.Length);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(991528, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/991528")]
        public async Task MSBuildProjectShouldHandleInvalidCodePageProperty()
        {
            var files = new FileSet(
                ("Encoding.csproj", Resources.ProjectFiles.CSharp.Encoding.Replace("<CodePage>ReplaceMe</CodePage>", "<CodePage>-1</CodePage>")),
                ("class1.cs", "//\u201C"));

            CreateFiles(files);

            var projPath = GetSolutionFileName("Encoding.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projPath);
                var document = project.Documents.First(d => d.Name == "class1.cs");
                var text = await document.GetTextAsync();
                Assert.Equal(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), text.Encoding);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(991528, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/991528")]
        public async Task MSBuildProjectShouldHandleInvalidCodePageProperty2()
        {
            var files = new FileSet(
                ("Encoding.csproj", Resources.ProjectFiles.CSharp.Encoding.Replace("<CodePage>ReplaceMe</CodePage>", "<CodePage>Broken</CodePage>")),
                ("class1.cs", "//\u201C"));

            CreateFiles(files);

            var projPath = GetSolutionFileName("Encoding.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projPath);
                var document = project.Documents.First(d => d.Name == "class1.cs");
                var text = await document.GetTextAsync();
                Assert.Equal(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), text.Encoding);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(991528, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/991528")]
        public async Task MSBuildProjectShouldHandleDefaultCodePageProperty()
        {
            var files = new FileSet(
                ("Encoding.csproj", Resources.ProjectFiles.CSharp.Encoding.Replace("<CodePage>ReplaceMe</CodePage>", string.Empty)),
                ("class1.cs", "//\u201C"));

            CreateFiles(files);

            var projPath = GetSolutionFileName("Encoding.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projPath);
                var document = project.Documents.First(d => d.Name == "class1.cs");
                var text = await document.GetTextAsync();
                Assert.Equal(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), text.Encoding);
                Assert.Equal("//\u201C", text.ToString());
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled), typeof(x86)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(981208, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/981208")]
        [WorkItem(28639, "https://github.com/dotnet/roslyn/issues/28639")]
        public void DisposeMSBuildWorkspaceAndServicesCollected()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());

            var sol = MSBuildWorkspace.Create().OpenSolutionAsync(GetSolutionFileName("TestSolution.sln")).Result;
            var workspace = sol.Workspace;
            var project = sol.Projects.First();
            var document = project.Documents.First();
            var tree = document.GetSyntaxTreeAsync().Result;
            var type = tree.GetRoot().DescendantTokens().First(t => t.ToString() == "class").Parent;
            var compilation = document.GetSemanticModelAsync().WaitAndGetResult_CanCallOnBackground(CancellationToken.None);
            Assert.NotNull(type);
            Assert.StartsWith("public class CSharpClass", type.ToString(), StringComparison.Ordinal);
            Assert.NotNull(compilation);

            // MSBuildWorkspace doesn't have a cache service
            Assert.Null(workspace.CurrentSolution.Services.CacheService);

            var weakSolution = ObjectReference.Create(sol);
            var weakCompilation = ObjectReference.Create(compilation);

            sol.Workspace.Dispose();
            project = null;
            document = null;
            tree = null;
            type = null;
            sol = null;
            compilation = null;

            weakSolution.AssertReleased();
            weakCompilation.AssertReleased();
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(1088127, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/1088127")]
        public async Task MSBuildWorkspacePreservesEncoding()
        {
            var encoding = Encoding.BigEndianUnicode;
            var fileContent = @"//“
class C { }";
            var files = new FileSet(
                ("Encoding.csproj", Resources.ProjectFiles.CSharp.Encoding.Replace("<CodePage>ReplaceMe</CodePage>", string.Empty)),
                ("class1.cs", encoding.GetBytesWithPreamble(fileContent)));

            CreateFiles(files);
            var projPath = GetSolutionFileName("Encoding.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projPath);

                var document = project.Documents.First(d => d.Name == "class1.cs");

                // update root without first looking at text (no encoding is known)
                var gen = Editing.SyntaxGenerator.GetGenerator(document);
                var doc2 = document.WithSyntaxRoot(gen.CompilationUnit()); // empty CU
                var doc2text = await doc2.GetTextAsync();
                Assert.Null(doc2text.Encoding);
                var doc2tree = await doc2.GetSyntaxTreeAsync();
                Assert.Null(doc2tree.Encoding);
                Assert.Null(doc2tree.GetText().Encoding);

                // observe original text to discover encoding
                var text = await document.GetTextAsync();
                Assert.Equal(encoding.EncodingName, text.Encoding.EncodingName);
                Assert.Equal(fileContent, text.ToString());

                // update root blindly again, after observing encoding, see that now encoding is known
                var doc3 = document.WithSyntaxRoot(gen.CompilationUnit()); // empty CU
                var doc3text = await doc3.GetTextAsync();
                Assert.NotNull(doc3text.Encoding);
                Assert.Equal(encoding.EncodingName, doc3text.Encoding.EncodingName);
                var doc3tree = await doc3.GetSyntaxTreeAsync();
                Assert.Equal(doc3text.Encoding, doc3tree.GetText().Encoding);
                Assert.Equal(doc3text.Encoding, doc3tree.Encoding);

                // change doc to have no encoding, still succeeds at writing to disk with old encoding
                var root = await document.GetSyntaxRootAsync();
                var noEncodingDoc = document.WithText(SourceText.From(text.ToString(), encoding: null));
                var noEncodingDocText = await noEncodingDoc.GetTextAsync();
                Assert.Null(noEncodingDocText.Encoding);

                // apply changes (this writes the changed document)
                var noEncodingSolution = noEncodingDoc.Project.Solution;
                Assert.True(noEncodingSolution.Workspace.TryApplyChanges(noEncodingSolution));

                // prove the written document still has the same encoding
                var filePath = GetSolutionFileName("Class1.cs");
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var reloadedText = EncodedStringText.Create(stream);
                    Assert.Equal(encoding.EncodingName, reloadedText.Encoding.EncodingName);
                }
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestAddRemoveMetadataReference_GAC()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());

            var projFile = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");
            var projFileText = File.ReadAllText(projFile);
            Assert.Equal(false, projFileText.Contains(@"System.Xaml"));

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solutionFilePath = GetSolutionFileName("TestSolution.sln");
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.Projects.First();

                var mref = MetadataReference.CreateFromFile(typeof(System.Xaml.XamlObjectReader).Assembly.Location);

                // add reference to System.Xaml
                workspace.TryApplyChanges(project.AddMetadataReference(mref).Solution);
                projFileText = File.ReadAllText(projFile);
                Assert.Contains(@"<Reference Include=""System.Xaml,", projFileText);

                // remove reference to System.Xaml
                workspace.TryApplyChanges(workspace.CurrentSolution.GetProject(project.Id).RemoveMetadataReference(mref).Solution);
                projFileText = File.ReadAllText(projFile);
                Assert.DoesNotContain(@"<Reference Include=""System.Xaml,", projFileText);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestAddRemoveMetadataReference_NonGACorRefAssembly()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"References\MyAssembly.dll", Resources.Dlls.EmptyLibrary));

            var projFile = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");
            var projFileText = File.ReadAllText(projFile);
            Assert.Equal(false, projFileText.Contains(@"MyAssembly"));

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solutionFilePath = GetSolutionFileName("TestSolution.sln");
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.Projects.First();

                var myAssemblyPath = GetSolutionFileName(@"References\MyAssembly.dll");
                var mref = MetadataReference.CreateFromFile(myAssemblyPath);

                // add reference to MyAssembly.dll
                workspace.TryApplyChanges(project.AddMetadataReference(mref).Solution);
                projFileText = File.ReadAllText(projFile);
                Assert.Contains(@"<Reference Include=""MyAssembly""", projFileText);
                Assert.Contains(@"<HintPath>..\References\MyAssembly.dll", projFileText);

                // remove reference MyAssembly.dll
                workspace.TryApplyChanges(workspace.CurrentSolution.GetProject(project.Id).RemoveMetadataReference(mref).Solution);
                projFileText = File.ReadAllText(projFile);
                Assert.DoesNotContain(@"<Reference Include=""MyAssembly""", projFileText);
                Assert.DoesNotContain(@"<HintPath>..\References\MyAssembly.dll", projFileText);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestAddRemoveAnalyzerReference()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"Analyzers\MyAnalyzer.dll", Resources.Dlls.EmptyLibrary));

            var projFile = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");
            var projFileText = File.ReadAllText(projFile);
            Assert.Equal(false, projFileText.Contains(@"<Analyzer Include=""..\Analyzers\MyAnalyzer.dll"));

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solutionFilePath = GetSolutionFileName("TestSolution.sln");
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.Projects.First();

                var myAnalyzerPath = GetSolutionFileName(@"Analyzers\MyAnalyzer.dll");
                var aref = new AnalyzerFileReference(myAnalyzerPath, new InMemoryAssemblyLoader());

                // add reference to MyAnalyzer.dll
                workspace.TryApplyChanges(project.AddAnalyzerReference(aref).Solution);
                projFileText = File.ReadAllText(projFile);
                Assert.Contains(@"<Analyzer Include=""..\Analyzers\MyAnalyzer.dll", projFileText);

                // remove reference MyAnalyzer.dll
                workspace.TryApplyChanges(workspace.CurrentSolution.GetProject(project.Id).RemoveAnalyzerReference(aref).Solution);
                projFileText = File.ReadAllText(projFile);
                Assert.DoesNotContain(@"<Analyzer Include=""..\Analyzers\MyAnalyzer.dll", projFileText);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(1101040, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/1101040")]
        public async Task TestOpenProject_BadLink()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", Resources.ProjectFiles.CSharp.BadLink));

            var projectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var proj = await workspace.OpenProjectAsync(projectFilePath);
                var docs = proj.Documents.ToList();
                Assert.Equal(3, docs.Count);
            }
        }

        [ConditionalFact(typeof(IsEnglishLocal), typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_BadElement()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", Resources.ProjectFiles.CSharp.BadElement));

            var projectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var proj = await workspace.OpenProjectAsync(projectFilePath);

                var diagnostic = Assert.Single(workspace.Diagnostics);
                Assert.StartsWith("Msbuild failed", diagnostic.Message);

                Assert.Empty(proj.DocumentIds);
            }
        }

        [ConditionalFact(typeof(IsEnglishLocal), typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_BadTaskImport()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", Resources.ProjectFiles.CSharp.BadTasks));

            var projectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var proj = await workspace.OpenProjectAsync(projectFilePath);

                var diagnostic = Assert.Single(workspace.Diagnostics);
                Assert.StartsWith("Msbuild failed", diagnostic.Message);

                Assert.Empty(proj.DocumentIds);
            }
        }

        [ConditionalFact(typeof(IsEnglishLocal), typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenSolution_BadTaskImport()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", Resources.ProjectFiles.CSharp.BadTasks));

            var solutionFilePath = GetSolutionFileName(@"TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);

                var diagnostic = Assert.Single(workspace.Diagnostics);
                Assert.StartsWith("Msbuild failed", diagnostic.Message);

                var project = Assert.Single(solution.Projects);
                Assert.Empty(project.DocumentIds);
            }
        }

        [ConditionalFact(typeof(IsEnglishLocal), typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_MsbuildError()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", Resources.ProjectFiles.CSharp.MsbuildError));

            var projectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var proj = await workspace.OpenProjectAsync(projectFilePath);

                var diagnostic = Assert.Single(workspace.Diagnostics);
                Assert.StartsWith("Msbuild failed", diagnostic.Message);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_WildcardsWithLink()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", Resources.ProjectFiles.CSharp.Wildcards));

            var projectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var proj = await workspace.OpenProjectAsync(projectFilePath);

                // prove that the file identified with a wildcard and remapped to a computed link is named correctly.
                Assert.Contains(proj.Documents, d => d.Name == "AssemblyInfo.cs");
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_CommandLineArgsHaveNoErrors()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());

            using (var workspace = CreateMSBuildWorkspace())
            {
                var loader = workspace.Services
                    .GetLanguageServices(LanguageNames.CSharp)
                    .GetRequiredService<IProjectFileLoader>();

                var projectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");

                var buildManager = new ProjectBuildManager(ImmutableDictionary<string, string>.Empty);
                buildManager.StartBatchBuild();

                var projectFile = await loader.LoadProjectFileAsync(projectFilePath, buildManager, CancellationToken.None);
                var projectFileInfo = (await projectFile.GetProjectFileInfosAsync(CancellationToken.None)).Single();
                buildManager.EndBatchBuild();

                var commandLineParser = workspace.Services
                    .GetLanguageServices(loader.Language)
                    .GetRequiredService<ICommandLineParserService>();

                var projectDirectory = Path.GetDirectoryName(projectFilePath);
                var commandLineArgs = commandLineParser.Parse(
                    arguments: projectFileInfo.CommandLineArgs,
                    baseDirectory: projectDirectory,
                    isInteractive: false,
                    sdkDirectory: System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());

                Assert.Empty(commandLineArgs.Errors);
            }
        }

        [ConditionalFact(typeof(VisualStudioMSBuildInstalled)), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(29494, "https://github.com/dotnet/roslyn/issues/29494")]
        public async Task TestOpenProjectAsync_MalformedAdditionalFilePath()
        {
            var files = GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", Resources.ProjectFiles.CSharp.MallformedAdditionalFilePath)
                .WithFile(@"CSharpProject\ValidAdditionalFile.txt", Resources.SourceFiles.Text.ValidAdditionalFile);

            CreateFiles(files);

            var projectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projectFilePath);

                // Project should open without an exception being thrown.
                Assert.NotNull(project);

                Assert.Contains(project.AdditionalDocuments, doc => doc.Name == "COM1");
                Assert.Contains(project.AdditionalDocuments, doc => doc.Name == "TEST::");
            }
        }

        private class InMemoryAssemblyLoader : IAnalyzerAssemblyLoader
        {
            public void AddDependencyLocation(string fullPath)
            {
            }

            public Assembly LoadFromPath(string fullPath)
            {
                var bytes = File.ReadAllBytes(fullPath);
                return Assembly.Load(bytes);
            }
        }
    }
}
