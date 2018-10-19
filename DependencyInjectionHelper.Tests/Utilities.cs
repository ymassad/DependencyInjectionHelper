using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace DependencyInjectionHelper.Tests
{
    public static class Utilities
    {
 

        public static string NormalizeCode(string code)
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(code);
            var newRoot = syntaxTree.GetRoot().NormalizeWhitespace();

            return newRoot.ToString();
        }

        public static string MergeParts(params string[] parts)
        {
            return String.Join(Environment.NewLine, parts);
        }

        public static string InNamespace(string content, string @namespace)
        {
            return $@"namespace {@namespace}
{{
    {content}
}}";
        }

        public static string ApplyRefactoring(
            string content,
            Func<SyntaxNode, TextSpan> spanSelector,
            params MetadataReference[] additionalReferences)
        {
            return ApplyRefactoring(content, spanSelector, Maybe.NoValue, additionalReferences);
        }

        public static string ApplyRefactoring(
            string content,
            Func<SyntaxNode, TextSpan> spanSelector,
            Maybe<string> refactoringName,
            params MetadataReference[] additionalReferences)
        {
            return ApplyRefactoring(content, spanSelector, refactoringName, true, additionalReferences);
        }

        public static string ApplyRefactoring(
            string content,
            Func<SyntaxNode, TextSpan> spanSelector,
            Maybe<string> refactoringName,
            bool shouldThereBeRefactorings,
            params MetadataReference[] additionalReferences)
        {
            return ApplyRefactoring(
                content,
                spanSelector,
                refactoringName,
                shouldThereBeRefactorings,
                Maybe.NoValue,
                additionalReferences).firstFileContent;
        }

        public static (string firstFileContent, Maybe<string> secondFileContent) ApplyRefactoring(
            string content,
            Func<SyntaxNode, TextSpan> spanSelector,
            Maybe<string> refactoringName,
            bool shouldThereBeRefactorings,
            Maybe<string> secondFileContent,
            params MetadataReference[] additionalReferences)
        {
            var workspace = new AdhocWorkspace();

            var solution = workspace.CurrentSolution;

            var projectId = ProjectId.CreateNewId();

            solution = AddNewProjectToWorkspace(solution, "NewProject", projectId, additionalReferences);

            var documentId = DocumentId.CreateNewId(projectId);

            solution = AddNewSourceFile(solution, content, "NewFile.cs", documentId);

            Maybe<DocumentId> document2Id = default;

            if (secondFileContent.HasValue)
            {
                document2Id = DocumentId.CreateNewId(projectId);

                solution = AddNewSourceFile(solution, secondFileContent.GetValue(), "NewFile2.cs", document2Id.GetValue());
            }

            var document = solution.GetDocument(documentId);

            var syntaxNode = document.GetSyntaxRootAsync().Result;

            var span = spanSelector(syntaxNode);

            var refactoringActions = new List<CodeAction>();

            var refactoringContext =
                new CodeRefactoringContext(
                    document,
                    span,
                    action => refactoringActions.Add(action),
                    CancellationToken.None);

            var sut = new DependencyInjectionHelperCodeRefactoringProvider();

            sut.ComputeRefactoringsAsync(refactoringContext).Wait();
            
            
            if(!shouldThereBeRefactorings)
            {
                if (refactoringActions.Count > 0)
                {
                    throw new Exception("Some refactoring actions found");
                }

                return (content, secondFileContent);
            }


            if (refactoringActions.Count == 0)
                throw new Exception("No refactoring actions found");


            refactoringActions.ForEach(action =>
            {
                if (refactoringName.HasNoValue || action.Title == refactoringName.GetValue())
                {
                    var operations = action.GetOperationsAsync(CancellationToken.None).Result;

                    foreach (var operation in operations)
                    {
                        operation.Apply(workspace, CancellationToken.None);
                    }
                }
            });
            
            var updatedDocument = workspace.CurrentSolution.GetDocument(documentId);

            var updatedSecondDocument = document2Id
                .ChainValue(x => workspace.CurrentSolution.GetDocument(x));

            var updatedDocumentContent = updatedDocument.GetSyntaxRootAsync().Result.GetText().ToString();

            var updatedSecondDocumentContent =
                updatedSecondDocument.ChainValue(x => x.GetSyntaxRootAsync().Result.GetText().ToString());

            return (updatedDocumentContent, updatedSecondDocumentContent);
        }

        private static Solution AddNewSourceFile(
            Solution solution,
            string fileContent,
            string fileName,
            DocumentId documentId)
        {
            return solution.AddDocument(documentId, fileName, SourceText.From(fileContent));
        }

        private static Solution AddNewProjectToWorkspace(
            Solution solution, string projName, ProjectId projectId, params MetadataReference[] additionalReferences)
        {
            MetadataReference csharpSymbolsReference = MetadataReference.CreateFromFile(typeof(CSharpCompilation).Assembly.Location);
            MetadataReference codeAnalysisReference = MetadataReference.CreateFromFile(typeof(Compilation).Assembly.Location);

            MetadataReference corlibReference = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
            MetadataReference systemCoreReference = MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location);

            return
                solution.AddProject(
                    ProjectInfo.Create(
                            projectId,
                            VersionStamp.Create(),
                            projName,
                            projName,
                            LanguageNames.CSharp)
                        .WithMetadataReferences(new[]
                        {
                            corlibReference,
                            systemCoreReference,
                            csharpSymbolsReference,
                            codeAnalysisReference
                        }.Concat(additionalReferences).ToArray()));
        }

        public static TextSpan SelectSpanWhereClassIsDeclared (SyntaxNode rootNode, string className)
        {
            return rootNode.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .Single(x => x.Identifier.Text == className)
                .Span;
        }
    }
}
