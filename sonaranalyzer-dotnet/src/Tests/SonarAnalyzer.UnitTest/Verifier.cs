﻿/*
 * SonarAnalyzer for .NET
 * Copyright (C) 2015-2017 SonarSource SA
 * mailto: contact AT sonarsource DOT com
 *
 * This program is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 3 of the License, or (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with this program; if not, write to the Free Software Foundation,
 * Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.
 */

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using SonarAnalyzer.Common;
using SonarAnalyzer.Helpers;
using SonarAnalyzer.UnitTest.TestFramework;
using CS = Microsoft.CodeAnalysis.CSharp;
using VB = Microsoft.CodeAnalysis.VisualBasic;

namespace SonarAnalyzer.UnitTest
{
    internal static class Verifier
    {
        private static readonly MetadataReference systemAssembly = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        private static readonly MetadataReference systemLinqAssembly = MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location);
        private static readonly MetadataReference systemNetAssembly = MetadataReference.CreateFromFile(typeof(WebClient).Assembly.Location);
        internal static readonly MetadataReference SystemImmutableAssembly = MetadataReference.CreateFromFile(typeof(ImmutableArray).Assembly.Location);

        private const string NONCOMPLIANT_START = "Noncompliant";
        private const string FIXED_MESSAGE = "Fixed";
        private const string NONCOMPLIANT_PATTERN = NONCOMPLIANT_START + @".*";

        private const string GeneratedAssemblyName = "foo";
        private const string TestAssemblyName = "fooTest";
        private const string AnalyzerFailedDiagnosticId = "AD0001";
        private const string CSharpFileExtension = ".cs";

        #region Verify

        public static void VerifyNoExceptionThrown(string path,
            IEnumerable<DiagnosticAnalyzer> diagnosticAnalyzers)
        {
            using (var workspace = new AdhocWorkspace())
            {
                var file = new FileInfo(path);
                var project = CreateProject(file, GeneratedAssemblyName, workspace).AddDocument(file);
                var compilation = project.GetCompilationAsync().Result;
                var diagnostics = GetAllDiagnostics(compilation, diagnosticAnalyzers);
                VerifyNoExceptionThrown(diagnostics);
            }
        }

        public static void VerifyAnalyzer(string path, SonarDiagnosticAnalyzer diagnosticAnalyzer, ParseOptions options = null,
            params MetadataReference[] additionalReferences)
        {
            VerifyAnalyzer(new[] { path }, diagnosticAnalyzer, options, additionalReferences);
        }

        public static void VerifyAnalyzer(IEnumerable<string> paths, SonarDiagnosticAnalyzer diagnosticAnalyzer, ParseOptions options = null,
            params MetadataReference[] additionalReferences)
        {
            if (paths == null || !paths.Any())
            {
                throw new ArgumentException("Please specify at least one file path to analyze.", nameof(paths));
            }

            var files = paths.Select(path => new FileInfo(path)).ToList();
            if (files.Select(file => file.Extension).Distinct().Count() != 1)
            {
                throw new ArgumentException("Please use a collection of paths with the same extension", nameof(paths));
            }

            var parseOptions = files.SelectMany(file => GetParseOptionsAlternatives(options, file.Extension)).Distinct();

            var issueLocationCollector = new IssueLocationCollector();

            using (var workspace = new AdhocWorkspace())
            {
                var project = CreateProject(files[0], GeneratedAssemblyName, workspace, additionalReferences);
                files.ForEach(file => project = project.AddDocument(file)); // side effect on purpose (project is immutable)

                foreach (var parseOption in parseOptions)
                {
                    if (parseOption != null)
                    {
                        project = project.WithParseOptions(parseOption);
                    }

                    var compilation = project.GetCompilationAsync().Result;

                    var diagnostics = GetDiagnostics(compilation, diagnosticAnalyzer);

                    var expectedIssues = issueLocationCollector
                        .GetExpectedIssueLocations(compilation.SyntaxTrees.Skip(1).First().GetText().Lines)
                        .ToList();

                    foreach (var diagnostic in diagnostics)
                    {
                        string issueId;
                        VerifyIssue(expectedIssues, issue => issue.IsPrimary, diagnostic.Location, diagnostic.GetMessage(), out issueId);

                        diagnostic.AdditionalLocations
                            .Select((location, i) => diagnostic.GetSecondaryLocation(i))
                            .OrderBy(x => x.Location.GetLineNumberToReport())
                            .ThenBy(x => x.Location.GetLineSpan().StartLinePosition.Character)
                            .ToList()
                            .ForEach(secondaryLocation =>
                            {
                                VerifyIssue(expectedIssues, issue => issue.IssueId == issueId && !issue.IsPrimary,
                                    secondaryLocation.Location, secondaryLocation.Message, out issueId);
                            });
                    }

                    if (expectedIssues.Count != 0)
                    {
                        Execute.Assertion.FailWith($"Issue expected but not raised on line(s) {string.Join(",", expectedIssues.Select(i => i.LineNumber))}.");
                    }
                }
            }
        }

        private static void VerifyIssue(IList<IIssueLocation> expectedIssues, Func<IIssueLocation, bool> issueFilter, Location location, string message, out string issueId)
        {
            var lineNumber = location.GetLineNumberToReport();

            var expectedIssue = expectedIssues
                .Where(issueFilter)
                .FirstOrDefault(issue => issue.LineNumber == lineNumber);

            if (expectedIssue == null)
            {
                Execute.Assertion.FailWith($"Issue with message '{message}' not expected on line {lineNumber}");
            }

            if (expectedIssue.Message != null && expectedIssue.Message != message)
            {
                Execute.Assertion.FailWith($"Expected message on line {lineNumber} to be '{expectedIssue.Message}', but got '{message}'.");
            }

            var diagnosticStart = location.GetLineSpan().StartLinePosition.Character;

            if (expectedIssue.Start.HasValue && expectedIssue.Start != diagnosticStart)
            {
                Execute.Assertion.FailWith(
                    $"Expected issue on line {lineNumber} to start on column {expectedIssue.Start} but got column {diagnosticStart}.");
            }

            if (expectedIssue.Length.HasValue && expectedIssue.Length != location.SourceSpan.Length)
            {
                Execute.Assertion.FailWith(
                    $"Expected issue on line {lineNumber} to have a length of {expectedIssue.Length} but got a length of {location.SourceSpan.Length}).");
            }

            expectedIssues.Remove(expectedIssue);

            issueId = expectedIssue.IssueId;
        }

        public static void VerifyNoIssueReportedInTest(string path, SonarDiagnosticAnalyzer diagnosticAnalyzer)
        {
            VerifyNoIssueReported(path, TestAssemblyName, diagnosticAnalyzer);
        }

        public static void VerifyNoIssueReported(string path, SonarDiagnosticAnalyzer diagnosticAnalyzer)
        {
            VerifyNoIssueReported(path, GeneratedAssemblyName, diagnosticAnalyzer);
        }

        public static void VerifyCodeFix(string path, string pathToExpected, SonarDiagnosticAnalyzer diagnosticAnalyzer,
            SonarCodeFixProvider codeFixProvider)
        {
            VerifyCodeFix(path, pathToExpected, pathToExpected, diagnosticAnalyzer, codeFixProvider, null);
        }

        public static void VerifyCodeFix(string path, string pathToExpected, string pathToBatchExpected, SonarDiagnosticAnalyzer diagnosticAnalyzer,
            SonarCodeFixProvider codeFixProvider)
        {
            VerifyCodeFix(path, pathToExpected, pathToBatchExpected, diagnosticAnalyzer, codeFixProvider, null);
        }

        public static void VerifyCodeFix(string path, string pathToExpected, SonarDiagnosticAnalyzer diagnosticAnalyzer,
            SonarCodeFixProvider codeFixProvider, string codeFixTitle)
        {
            VerifyCodeFix(path, pathToExpected, pathToExpected, diagnosticAnalyzer, codeFixProvider, codeFixTitle);
        }

        public static void VerifyCodeFix(string path, string pathToExpected, string pathToBatchExpected, SonarDiagnosticAnalyzer diagnosticAnalyzer,
            SonarCodeFixProvider codeFixProvider, string codeFixTitle)
        {
            using (var workspace = new AdhocWorkspace())
            {
                var file = new FileInfo(path);
                var parseOptions = GetParseOptionsWithDifferentLanguageVersions(null, file.Extension);

                foreach (var parseOption in parseOptions)
                {
                    var document = CreateProject(file, GeneratedAssemblyName, workspace)
                        .AddDocument(file, true)
                        .Documents
                        .Single(d => d.Name == file.Name);
                    RunCodeFixWhileDocumentChanges(diagnosticAnalyzer, codeFixProvider, codeFixTitle, document, parseOption, pathToExpected);
                }
            }

            VerifyFixAllCodeFix(path, pathToBatchExpected, diagnosticAnalyzer, codeFixProvider, codeFixTitle);
        }


        #endregion

        #region Generic helper

        private static void VerifyNoIssueReported(string path, string assemblyName, DiagnosticAnalyzer diagnosticAnalyzer)
        {
            using (var workspace = new AdhocWorkspace())
            {
                var file = new FileInfo(path);
                var project = CreateProject(file, assemblyName, workspace).AddDocument(file);
                var compilation = project.GetCompilationAsync().Result;
                var diagnostics = GetDiagnostics(compilation, diagnosticAnalyzer);

                diagnostics.Should().BeEmpty();
            }
        }

        private static void VerifyFixAllCodeFix(string path, string pathToExpected, DiagnosticAnalyzer diagnosticAnalyzer,
            CodeFixProvider codeFixProvider, string codeFixTitle)
        {
            var fixAllProvider = codeFixProvider.GetFixAllProvider();
            if (fixAllProvider == null)
            {
                return;
            }

            using (var workspace = new AdhocWorkspace())
            {
                var file = new FileInfo(path);
                var parseOptions = GetParseOptionsWithDifferentLanguageVersions(null, file.Extension);

                foreach (var parseOption in parseOptions)
                {
                    var document = CreateProject(file, GeneratedAssemblyName, workspace)
                        .AddDocument(file, true)
                        .Documents
                        .Single(d => d.Name == file.Name);
                    RunFixAllProvider(diagnosticAnalyzer, codeFixProvider, codeFixTitle, fixAllProvider, document, parseOption, pathToExpected);
                }
            }
        }

        private static Project CreateProject(FileInfo file, string assemblyName,
            AdhocWorkspace workspace, params MetadataReference[] additionalReferences)
        {
            var language = file.Extension == CSharpFileExtension
                ? LanguageNames.CSharp
                : LanguageNames.VisualBasic;

            var project = workspace.CurrentSolution.AddProject(assemblyName, $"{assemblyName}.dll", language)
                .AddMetadataReference(systemAssembly)
                .AddMetadataReference(systemLinqAssembly)
                .AddMetadataReference(systemNetAssembly)
                .AddMetadataReferences(additionalReferences);

            // adding an extra file to the project
            // this won't trigger any issues, but it keeps a reference to the original ParseOption, so
            // if an analyzer/codefix changes the language version, Roslyn throws an ArgumentException
            project = project.AddDocument("ExtraEmptyFile.g" + file.Extension, string.Empty).Project;

            return project;
        }

        private static Project AddDocument(this Project project, FileInfo file,
            bool removeAnalysisComments = false)
        {
            var lines = File.ReadAllText(file.FullName, Encoding.UTF8)
                .Split(new[] { Environment.NewLine }, StringSplitOptions.None);

            if (removeAnalysisComments)
            {
                lines = lines.Where(IssueLocationCollector.IsNotIssueLocationLine)
                    .Select(ReplaceNonCompliantComment)
                    .ToArray();
            }

            var text = string.Join(Environment.NewLine, lines);

            return project.AddDocument(file.Name, text).Project;
        }

        private static string ReplaceNonCompliantComment(string line)
        {
            var match = Regex.Match(line, IssueLocationCollector.ISSUE_LOCATION_PATTERN);
            if (!match.Success)
            {
                return line;
            }

            if (match.Groups["issueType"].Value == "Noncompliant")
            {
                var startIndex = line.IndexOf(match.Groups["issueType"].Value);
                return string.Concat(line.Remove(startIndex), FIXED_MESSAGE);
            }

            return line.Replace(match.Value, string.Empty).TrimEnd();
        }

        #endregion

        #region Analyzer helpers

        private static IEnumerable<ParseOptions> GetParseOptionsAlternatives(ParseOptions options, string fileExtension)
        {
            return GetParseOptionsWithDifferentLanguageVersions(options, fileExtension).Concat(new[] { options });
        }

        private static IEnumerable<ParseOptions> GetParseOptionsWithDifferentLanguageVersions(ParseOptions options, string fileExtension)
        {
            if (fileExtension == CSharpFileExtension)
            {
                if (options == null)
                {
                    var csOptions = new CS.CSharpParseOptions();
                    yield return csOptions.WithLanguageVersion(CS.LanguageVersion.CSharp6);
                    yield return csOptions.WithLanguageVersion(CS.LanguageVersion.CSharp5);
                }
                yield break;
            }

            var vbOptions = options as VB.VisualBasicParseOptions ?? new VB.VisualBasicParseOptions();
            yield return vbOptions.WithLanguageVersion(VB.LanguageVersion.VisualBasic14);
            yield return vbOptions.WithLanguageVersion(VB.LanguageVersion.VisualBasic12);
        }

        internal static IEnumerable<Diagnostic> GetDiagnostics(Compilation compilation,
            DiagnosticAnalyzer diagnosticAnalyzer)
        {
            var ids = new HashSet<string>(diagnosticAnalyzer.SupportedDiagnostics.Select(diagnostic => diagnostic.Id));

            var diagnostics = GetAllDiagnostics(compilation, new[] { diagnosticAnalyzer }).ToList();
            VerifyNoExceptionThrown(diagnostics);

            return diagnostics.Where(d => ids.Contains(d.Id));
        }

        private static void VerifyNoExceptionThrown(IEnumerable<Diagnostic> diagnostics)
        {
            diagnostics.Where(d => d.Id == AnalyzerFailedDiagnosticId).Should().BeEmpty();
        }

        private static IEnumerable<Diagnostic> GetAllDiagnostics(Compilation compilation,
            IEnumerable<DiagnosticAnalyzer> diagnosticAnalyzers)
        {
            using (var tokenSource = new CancellationTokenSource())
            {
                var compilationOptions = compilation.Language == LanguageNames.CSharp
                    ? (CompilationOptions)new CS.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true)
                    : new VB.VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
                var supportedDiagnostics = diagnosticAnalyzers
                        .SelectMany(analyzer => analyzer.SupportedDiagnostics)
                        .ToList();
                compilationOptions = compilationOptions.WithSpecificDiagnosticOptions(
                    supportedDiagnostics
                        .Select(diagnostic =>
                            new KeyValuePair<string, ReportDiagnostic>(diagnostic.Id, ReportDiagnostic.Warn))
                        .Union(
                            new[]
                            {
                                new KeyValuePair<string, ReportDiagnostic>(AnalyzerFailedDiagnosticId, ReportDiagnostic.Error)
                            }));

                var compilationWithOptions = compilation.WithOptions(compilationOptions);
                var compilationWithAnalyzer = compilationWithOptions
                    .WithAnalyzers(
                        diagnosticAnalyzers.ToImmutableArray(),
                        cancellationToken: tokenSource.Token);

                return compilationWithAnalyzer.GetAllDiagnosticsAsync().Result;
            }
        }

        #endregion

        #region Codefix helper

        private static void RunCodeFixWhileDocumentChanges(DiagnosticAnalyzer diagnosticAnalyzer, CodeFixProvider codeFixProvider,
            string codeFixTitle, Document document, ParseOptions parseOption, string pathToExpected)
        {
            var currentDocument = document;
            List<Diagnostic> diagnostics;
            string actualCode;
            CalculateDiagnosticsAndCode(diagnosticAnalyzer, currentDocument, parseOption, out diagnostics, out actualCode);

            diagnostics.Should().NotBeEmpty();

            string codeBeforeFix;
            var codeFixExecutedAtLeastOnce = false;

            do
            {
                codeBeforeFix = actualCode;

                var codeFixExecuted = false;
                for (int diagnosticIndexToFix = 0; !codeFixExecuted && diagnosticIndexToFix < diagnostics.Count; diagnosticIndexToFix++)
                {
                    var codeActionsForDiagnostic = GetCodeActionsForDiagnostic(codeFixProvider, currentDocument, diagnostics[diagnosticIndexToFix]);

                    CodeAction codeActionToExecute;
                    if (TryGetCodeActionToApply(codeFixTitle, codeActionsForDiagnostic, out codeActionToExecute))
                    {
                        currentDocument = ApplyCodeFix(currentDocument, codeActionToExecute);
                        CalculateDiagnosticsAndCode(diagnosticAnalyzer, currentDocument, parseOption, out diagnostics, out actualCode);

                        codeFixExecutedAtLeastOnce = true;
                        codeFixExecuted = true;
                    }
                }
            } while (codeBeforeFix != actualCode);

            codeFixExecutedAtLeastOnce.Should().BeTrue();
            actualCode.Should().Be(File.ReadAllText(pathToExpected));
        }

        private static void RunFixAllProvider(DiagnosticAnalyzer diagnosticAnalyzer, CodeFixProvider codeFixProvider,
            string codeFixTitle, FixAllProvider fixAllProvider, Document document, ParseOptions parseOption, string pathToExpected)
        {
            var currentDocument = document;
            List<Diagnostic> diagnostics;
            string actualCode;
            CalculateDiagnosticsAndCode(diagnosticAnalyzer, currentDocument, parseOption, out diagnostics, out actualCode);

            diagnostics.Should().NotBeEmpty();

            var fixAllDiagnosticProvider = new FixAllDiagnosticProvider(
                codeFixProvider.FixableDiagnosticIds.ToImmutableHashSet(),
                (doc, ids, ct) => Task.FromResult(
                    GetDiagnostics(currentDocument.Project.GetCompilationAsync(ct).Result, diagnosticAnalyzer)),
                null);
            var fixAllContext = new FixAllContext(currentDocument, codeFixProvider, FixAllScope.Document,
                codeFixTitle,
                codeFixProvider.FixableDiagnosticIds,
                fixAllDiagnosticProvider,
                CancellationToken.None);
            var codeActionToExecute = fixAllProvider.GetFixAsync(fixAllContext).Result;

            codeActionToExecute.Should().NotBeNull();

            currentDocument = ApplyCodeFix(currentDocument, codeActionToExecute);

            CalculateDiagnosticsAndCode(diagnosticAnalyzer, currentDocument, parseOption, out diagnostics, out actualCode);
            actualCode.Should().Be(File.ReadAllText(pathToExpected));
        }

        private static void CalculateDiagnosticsAndCode(DiagnosticAnalyzer diagnosticAnalyzer, Document document, ParseOptions parseOption,
            out List<Diagnostic> diagnostics,
            out string actualCode)
        {
            var project = document.Project;
            if (parseOption != null)
            {
                project = project.WithParseOptions(parseOption);
            }

            diagnostics = GetDiagnostics(project.GetCompilationAsync().Result, diagnosticAnalyzer).ToList();
            actualCode = document.GetSyntaxRootAsync().Result.GetText().ToString();
        }

        private static Document ApplyCodeFix(Document document, CodeAction codeAction)
        {
            var operations = codeAction.GetOperationsAsync(CancellationToken.None).Result;
            var solution = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution;
            return solution.GetDocument(document.Id);
        }

        private static bool TryGetCodeActionToApply(string codeFixTitle, IEnumerable<CodeAction> codeActions,
            out CodeAction codeAction)
        {
            codeAction = codeFixTitle != null
                ? codeActions.SingleOrDefault(action => action.Title == codeFixTitle)
                : codeActions.FirstOrDefault();

            return codeAction != null;
        }

        private static IEnumerable<CodeAction> GetCodeActionsForDiagnostic(CodeFixProvider codeFixProvider, Document document,
            Diagnostic diagnostic)
        {
            var actions = new List<CodeAction>();
            var context = new CodeFixContext(document, diagnostic, (a, d) => actions.Add(a), CancellationToken.None);

            codeFixProvider.RegisterCodeFixesAsync(context).Wait();
            return actions;
        }

        #endregion
    }
}
