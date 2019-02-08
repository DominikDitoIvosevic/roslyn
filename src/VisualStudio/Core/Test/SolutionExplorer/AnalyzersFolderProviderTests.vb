﻿' Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

Imports System.Collections.ObjectModel
Imports Microsoft.CodeAnalysis.Test.Utilities
Imports Microsoft.Internal.VisualStudio.PlatformUI
Imports Microsoft.VisualStudio.LanguageServices.Implementation
Imports Microsoft.VisualStudio.LanguageServices.Implementation.SolutionExplorer
Imports Microsoft.VisualStudio.LanguageServices.UnitTests.ProjectSystemShim.Framework
Imports Microsoft.VisualStudio.Shell
Imports Roslyn.Test.Utilities

Namespace Microsoft.VisualStudio.LanguageServices.UnitTests.SolutionExplorer
    <[UseExportProvider]>
    Public Class AnalyzersFolderProviderTests

        <WpfFact, Trait(Traits.Feature, Traits.Features.Diagnostics)>
        Public Sub CreateCollectionSource_NullItem()
            Using environment = New TestEnvironment()
                Dim provider As IAttachedCollectionSourceProvider =
                New AnalyzersFolderItemProvider(environment.ServiceProvider, Nothing)

                Dim collectionSource = provider.CreateCollectionSource(Nothing, KnownRelationships.Contains)

                Assert.Null(collectionSource)
            End Using
        End Sub

        <WpfFact, Trait(Traits.Feature, Traits.Features.Diagnostics)>
        Public Sub CreateCollectionSource_NullHierarchyIdentity()
            Using environment = New TestEnvironment()
                Dim provider As IAttachedCollectionSourceProvider =
                New AnalyzersFolderItemProvider(environment.ServiceProvider, Nothing)

                Dim hierarchyItem = New MockHierarchyItem With {.HierarchyIdentity = Nothing}

                Dim collectionSource = provider.CreateCollectionSource(hierarchyItem, KnownRelationships.Contains)

                Assert.Null(collectionSource)
            End Using
        End Sub
    End Class
End Namespace

