﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeFixes.Suppression;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Editor.UnitTests.CodeActions;
using Microsoft.CodeAnalysis.Editor.UnitTests.Workspaces;
using Microsoft.CodeAnalysis.GenerateType;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Utilities;
using Microsoft.CodeAnalysis.Test.Utilities;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.UnitTests;
using Microsoft.CodeAnalysis.UnitTests.Diagnostics;
using Roslyn.Test.Utilities;
using Roslyn.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.Editor.UnitTests.Diagnostics
{
    public abstract partial class AbstractUserDiagnosticTest : AbstractCodeActionOrUserDiagnosticTest
    {
        internal abstract Task<(ImmutableArray<Diagnostic>, ImmutableArray<CodeAction>, CodeAction actionToInvoke)> GetDiagnosticAndFixesAsync(
            TestWorkspace workspace, TestParameters parameters);

        internal abstract Task<IEnumerable<Diagnostic>> GetDiagnosticsAsync(
            TestWorkspace workspace, TestParameters parameters);

        protected async Task TestDiagnosticsAsync(
            string initialMarkup, TestParameters parameters = default, params DiagnosticDescription[] expected)
        {
            using (var workspace = CreateWorkspaceFromOptions(initialMarkup, parameters))
            {
                var diagnostics = await GetDiagnosticsAsync(workspace, parameters).ConfigureAwait(false);

                // Special case for single diagnostic reported with annotated span.
                if (expected.Length == 1)
                {
                    var hostDocumentsWithAnnotations = workspace.Documents.Where(d => d.SelectedSpans.Any());
                    if (hostDocumentsWithAnnotations.Count() == 1)
                    {
                        var expectedSpan = hostDocumentsWithAnnotations.Single().SelectedSpans.Single();

                        Assert.Equal(1, diagnostics.Count());
                        var diagnostic = diagnostics.Single();

                        var actualSpan = diagnostic.Location.SourceSpan;
                        Assert.Equal(expectedSpan, actualSpan);

                        Assert.Equal(expected[0].Code, diagnostic.Id);
                        return;
                    }
                }

                DiagnosticExtensions.Verify(diagnostics, expected);
            }
        }

        protected override async Task<(ImmutableArray<CodeAction>, CodeAction actionToInvoke)> GetCodeActionsAsync(
            TestWorkspace workspace, TestParameters parameters)
        {
            var (_, actions, actionToInvoke) = await GetDiagnosticAndFixesAsync(workspace, parameters);
            return (actions, actionToInvoke);
        }

        protected override async Task<ImmutableArray<Diagnostic>> GetDiagnosticsWorkerAsync(
            TestWorkspace workspace, TestParameters parameters)
        {
            var (dxs, _, _) = await GetDiagnosticAndFixesAsync(workspace, parameters);
            return dxs;
        }

        protected Document GetDocumentAndSelectSpan(TestWorkspace workspace, out TextSpan span)
        {
            var hostDocument = workspace.Documents.Single(d => d.SelectedSpans.Any());
            span = hostDocument.SelectedSpans.Single();
            return workspace.CurrentSolution.GetDocument(hostDocument.Id);
        }

        protected bool TryGetDocumentAndSelectSpan(TestWorkspace workspace, out Document document, out TextSpan span)
        {
            var hostDocument = workspace.Documents.FirstOrDefault(d => d.SelectedSpans.Any());
            if (hostDocument == null)
            {
                // If there wasn't a span, see if there was a $$ caret.  we'll create an empty span
                // there if so.
                hostDocument = workspace.Documents.FirstOrDefault(d => d.CursorPosition != null);
                if (hostDocument == null)
                {
                    document = null;
                    span = default;
                    return false;
                }

                span = new TextSpan(hostDocument.CursorPosition.Value, 0);
                document = workspace.CurrentSolution.GetDocument(hostDocument.Id);
                return true;
            }

            span = hostDocument.SelectedSpans.Single();
            document = workspace.CurrentSolution.GetDocument(hostDocument.Id);
            return true;
        }

        protected Document GetDocumentAndAnnotatedSpan(TestWorkspace workspace, out string annotation, out TextSpan span)
        {
            var annotatedDocuments = workspace.Documents.Where(d => d.AnnotatedSpans.Any());
            Debug.Assert(!annotatedDocuments.IsEmpty(), "No annotated span found");
            var hostDocument = annotatedDocuments.Single();
            var annotatedSpan = hostDocument.AnnotatedSpans.Single();
            annotation = annotatedSpan.Key;
            span = annotatedSpan.Value.Single();
            return workspace.CurrentSolution.GetDocument(hostDocument.Id);
        }

        protected FixAllScope? GetFixAllScope(string annotation)
        {
            if (annotation == null)
            {
                return null;
            }

            switch (annotation)
            {
                case "FixAllInDocument":
                    return FixAllScope.Document;

                case "FixAllInProject":
                    return FixAllScope.Project;

                case "FixAllInSolution":
                    return FixAllScope.Solution;

                case "FixAllInSelection":
                    return FixAllScope.Custom;
            }

            throw new InvalidProgramException("Incorrect FixAll annotation in test");
        }

        internal async Task<(ImmutableArray<Diagnostic>, ImmutableArray<CodeAction>, CodeAction actionToInvoke)> GetDiagnosticAndFixesAsync(
            IEnumerable<Diagnostic> diagnostics,
            DiagnosticAnalyzer provider,
            CodeFixProvider fixer,
            TestDiagnosticAnalyzerDriver testDriver,
            Document document,
            TextSpan span,
            string annotation,
            int index)
        {
            if (diagnostics.IsEmpty())
            {
                return (ImmutableArray<Diagnostic>.Empty, ImmutableArray<CodeAction>.Empty, null);
            }

            FixAllScope? scope = GetFixAllScope(annotation);
            return await GetDiagnosticAndFixesAsync(
                diagnostics, provider, fixer, testDriver, document, span, scope, index);
        }

        private async Task<(ImmutableArray<Diagnostic>, ImmutableArray<CodeAction>, CodeAction actionToinvoke)> GetDiagnosticAndFixesAsync(
            IEnumerable<Diagnostic> diagnostics,
            DiagnosticAnalyzer provider,
            CodeFixProvider fixer,
            TestDiagnosticAnalyzerDriver testDriver,
            Document document,
            TextSpan span,
            FixAllScope? scope,
            int index)
        {
            Assert.NotEmpty(diagnostics);

            var intersectingDiagnostics = diagnostics.Where(d => d.Location.SourceSpan.IntersectsWith(span))
                                                     .ToImmutableArray();

            var fixes = new List<CodeFix>();

            foreach (var diagnostic in intersectingDiagnostics)
            {
                var context = new CodeFixContext(
                    document, diagnostic,
                    (a, d) => fixes.Add(new CodeFix(document.Project, a, d)),
                    CancellationToken.None);

                await fixer.RegisterCodeFixesAsync(context);
            }

            var actions = fixes.SelectAsArray(f => f.Action);

            actions = actions.SelectMany(a => a is TopLevelSuppressionCodeAction
                ? a.NestedCodeActions
                : ImmutableArray.Create(a)).ToImmutableArray();

            actions = MassageActions(actions);

            if (scope == null)
            {
                // Simple code fix.
                return (intersectingDiagnostics, actions, actions.Length == 0 ? null : actions[index]);
            }
            else
            {

                var equivalenceKey = actions[index].EquivalenceKey;

                // Fix all fix.
                var fixAllProvider = fixer.GetFixAllProvider();
                Assert.NotNull(fixAllProvider);

                var fixAllState = GetFixAllState(
                    fixAllProvider, diagnostics, provider, fixer, testDriver,
                    document, scope.Value, equivalenceKey);
                var fixAllContext = fixAllState.CreateFixAllContext(new ProgressTracker(), CancellationToken.None);
                var fixAllFix = await fixAllProvider.GetFixAsync(fixAllContext);

                // We have collapsed the fixes down to the single fix-all fix, so we just let our
                // caller know they should pull that entry out of the result.
                return (intersectingDiagnostics, ImmutableArray.Create(fixAllFix), fixAllFix);
            }
        }

        private async Task<string> GetEquivalenceKeyAsync(
            Document document, CodeFixProvider provider, ImmutableArray<Diagnostic> diagnostics)
        {
            if (diagnostics.Length == 0)
            {
                throw new InvalidOperationException("No diagnostics found intersecting with span.");
            }

            var fixes = new List<CodeFix>();
            var context = new CodeFixContext(
                document, diagnostics[0],
                (a, d) => fixes.Add(new CodeFix(document.Project, a, d)),
                CancellationToken.None);

            await provider.RegisterCodeFixesAsync(context).ConfigureAwait(false);
            if (fixes.Count == 0)
            {
                throw new InvalidOperationException("No fixes produced for diagnostic.");
            }

            return fixes[0].Action.EquivalenceKey;
        }

        private static FixAllState GetFixAllState(
            FixAllProvider fixAllProvider,
            IEnumerable<Diagnostic> diagnostics,
            DiagnosticAnalyzer provider,
            CodeFixProvider fixer,
            TestDiagnosticAnalyzerDriver testDriver,
            Document document,
            FixAllScope scope,
            string equivalenceKey)
        {
            Assert.NotEmpty(diagnostics);

            if (scope == FixAllScope.Custom)
            {
                // Bulk fixing diagnostics in selected scope.                    
                var diagnosticsToFix = ImmutableDictionary.CreateRange(SpecializedCollections.SingletonEnumerable(KeyValuePairUtil.Create(document, diagnostics.ToImmutableArray())));
                return FixAllState.Create(fixAllProvider, diagnosticsToFix, fixer, equivalenceKey);
            }

            var diagnostic = diagnostics.First();
            var diagnosticIds = ImmutableHashSet.Create(diagnostic.Id);
            var fixAllDiagnosticProvider = new FixAllDiagnosticProvider(provider, testDriver, diagnosticIds);

            return diagnostic.Location.IsInSource
                ? new FixAllState(fixAllProvider, document, fixer, scope, equivalenceKey, diagnosticIds, fixAllDiagnosticProvider)
                : new FixAllState(fixAllProvider, document.Project, fixer, scope, equivalenceKey, diagnosticIds, fixAllDiagnosticProvider);
        }

        protected Task TestActionCountInAllFixesAsync(
            string initialMarkup,
            int count,
            ParseOptions parseOptions = null,
            CompilationOptions compilationOptions = null,
            IDictionary<OptionKey, object> options = null,
            object fixProviderData = null)
        {
            return TestActionCountInAllFixesAsync(
                initialMarkup,
                new TestParameters(parseOptions, compilationOptions, options, fixProviderData),
                count);
        }

        private async Task TestActionCountInAllFixesAsync(
            string initialMarkup,
            TestParameters parameters,
            int count)
        {
            using (var workspace = CreateWorkspaceFromOptions(initialMarkup, parameters))
            {
                var (_, actions, _) = await GetDiagnosticAndFixesAsync(workspace, parameters);
                Assert.Equal(count, actions.Length);
            }
        }

        internal async Task TestSpansAsync(
            string initialMarkup,
            int index = 0,
            string diagnosticId = null,
            TestParameters parameters = default)
        {
            MarkupTestFile.GetSpans(initialMarkup, out var unused, out ImmutableArray<TextSpan> spansList);

            var expectedTextSpans = spansList.ToSet();
            using (var workspace = CreateWorkspaceFromOptions(initialMarkup, parameters))
            {
                ISet<TextSpan> actualTextSpans;
                if (diagnosticId == null)
                {
                    var (diagnostics, _, _) = await GetDiagnosticAndFixesAsync(workspace, parameters);
                    actualTextSpans = diagnostics.Select(d => d.Location.SourceSpan).ToSet();
                }
                else
                {
                    var diagnostics = await GetDiagnosticsAsync(workspace, parameters);
                    actualTextSpans = diagnostics.Where(d => d.Id == diagnosticId).Select(d => d.Location.SourceSpan).ToSet();
                }

                Assert.True(expectedTextSpans.SetEquals(actualTextSpans));
            }
        }
    }
}
