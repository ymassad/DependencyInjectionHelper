using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;

namespace DependencyInjectionHelper
{
    [ExportCodeRefactoringProvider(LanguageNames.CSharp, Name = nameof(DependencyInjectionHelperCodeRefactoringProvider)), Shared]
    public class DependencyInjectionHelperCodeRefactoringProvider : CodeRefactoringProvider
    {

        public static Func<ImmutableArray<Parameter>, ImmutableArray<WhatToDoWithParameter>> WhatToDoWithParameters;

        private static Dictionary<int, Type> ActionTypes = new Dictionary<int, Type>
        {
            [0] = typeof(Action),
            [1] = typeof(Action<>),
            [2] = typeof(Action<,>),
            [3] = typeof(Action<,,>),
            [4] = typeof(Action<,,,>),
            [5] = typeof(Action<,,,,>),
            [6] = typeof(Action<,,,,,>),
        };

        private static Dictionary<int, Type> FunctionTypes = new Dictionary<int, Type>
        {
            [0] = typeof(Func<>),
            [1] = typeof(Func<,>),
            [2] = typeof(Func<,,>),
            [3] = typeof(Func<,,,>),
            [4] = typeof(Func<,,,,>),
            [5] = typeof(Func<,,,,,>),
            [6] = typeof(Func<,,,,,,>)
        };

        static DependencyInjectionHelperCodeRefactoringProvider()
        {
            WhatToDoWithParameters =
                x => x.Select(_ => WhatToDoWithParameter.Keep)
                    .ToImmutableArray();
        }

        public static Maybe<InvocationExpressionSyntax> GetInvocation(IdentifierNameSyntax node)
        {
            switch (node.Parent)
            {
                case InvocationExpressionSyntax invocation:
                    return invocation;
                case MemberAccessExpressionSyntax memberAccess when memberAccess.Parent is InvocationExpressionSyntax invocation:
                    return invocation;
                default:
                    return Maybe.NoValue;
            }
        }

        public sealed override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);

            var semanticModel = await context.Document.GetSemanticModelAsync();

            var node = root.FindNode(context.Span);

            if (!(node is IdentifierNameSyntax nameSyntax))
                return;

            var invocation = GetInvocation(nameSyntax);

            if (invocation.HasNoValue)
                return;

            var action = new MyCodeAction(
                "Extract as a dependency",
                c => ExtractDependency(context.Document, invocation.GetValue(), nameSyntax, semanticModel, c));

            context.RegisterRefactoring(action);
        }

        private async Task<Solution> ExtractDependency(
            Document document,
            InvocationExpressionSyntax invocationSyntax,
            IdentifierNameSyntax invokedMethodIdentifierSyntax,
            SemanticModel semanticModel,
            CancellationToken cancellationToken)
        {
            var solution = document.Project.Solution;

            var containingMethod = invocationSyntax.Ancestors().OfType<MethodDeclarationSyntax>().First();

            var invocationOperation = semanticModel.GetOperation(invocationSyntax) as IInvocationOperation;

            if (invocationOperation == null)
                return solution;

            var (parameterListChange , replacementFunctionParameterName) =
                UpdateParameterListToContainNewDependency(document, invokedMethodIdentifierSyntax, semanticModel, invocationOperation, containingMethod);


            var invokedMethodName = invocationOperation.TargetMethod.Name;

            var nodesToReplace =
                new Dictionary<Document, List<NodeChange>>();

            void AddNewChangeToDocument(Document doc, NodeChange change)
            {
                nodesToReplace.GetOrAdd(doc, () => new List<NodeChange>()).Add(change);
            }

            AddNewChangeToDocument(document, new NodeChange(parameterListChange.OldNode, parameterListChange.NewNode));
            AddNewChangeToDocument(document, new NodeChange(invokedMethodIdentifierSyntax, invokedMethodIdentifierSyntax.WithIdentifier(SyntaxFactory.Identifier(replacementFunctionParameterName))));

            var changesToCallers = await GetChangesToCallers(semanticModel, cancellationToken, containingMethod, solution, invokedMethodName);

            foreach (var changeToCallers in changesToCallers)
            {
                AddNewChangeToDocument(changeToCallers.document, changeToCallers.change);
            }

            return await UpdateSolution(cancellationToken, solution, nodesToReplace);
        }

        private static async Task<Solution> UpdateSolution(
            CancellationToken cancellationToken,
            Solution solution,
            Dictionary<Document, List<NodeChange>> nodesToReplace)
        {
            Solution newSolution = solution;

            foreach (var doc in nodesToReplace.Keys)
            {
                var root = await doc.GetSyntaxRootAsync(cancellationToken);

                var newRoot = root.ReplaceNodes(nodesToReplace[doc].Select(x => x.OldNode),
                    (x, _) =>
                    {
                        return nodesToReplace[doc].Where(e => e.OldNode.Equals(x)).Select(e => e.NewNode).Single();
                    });

                newSolution = newSolution.WithDocumentSyntaxRoot(doc.Id, newRoot);
            }

            return newSolution;
        }

        private static async Task<List<(Document document, NodeChange change)>> GetChangesToCallers(
            SemanticModel semanticModel,
            CancellationToken cancellationToken,
            MethodDeclarationSyntax containingMethod,
            Solution solution,
            string invokedMethodName)
        {
            List<(Document document, NodeChange change)> localChanges = new List<(Document document, NodeChange change)>();

            var usagesOfContainingMethod =
                (await SymbolFinder.FindReferencesAsync(semanticModel.GetDeclaredSymbol(containingMethod), solution,
                    cancellationToken)).ToList();


            foreach (var reference in usagesOfContainingMethod.SelectMany(x => x.Locations))
            {
                var refDocument = reference.Document;

                var refDocumentRoot = await refDocument.GetSyntaxRootAsync(cancellationToken);

                var refNode = refDocumentRoot.FindNode(reference.Location.SourceSpan);

                if (!(refNode is IdentifierNameSyntax refNameSyntax))
                    continue;

                var inv = GetInvocation(refNameSyntax);

                if (inv.HasNoValue)
                    continue;

                var refInvocation = inv.GetValue();

                var oldArgumentList = refInvocation.ArgumentList;
                var newArgumentList = oldArgumentList.AddArguments(
                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName(invokedMethodName)));

                localChanges.Add((refDocument, new NodeChange(oldArgumentList, newArgumentList)));
            }

            return localChanges;
        }

        private static (NodeChange change, string replacementFunctionParameterName) UpdateParameterListToContainNewDependency(
            Document document,
            IdentifierNameSyntax invokedMethodIdentifierSyntax,
            SemanticModel semanticModel,
            IInvocationOperation invocationOperation,
            MethodDeclarationSyntax containingMethod)
        {
            var syntaxGenerator = SyntaxGenerator.GetGenerator(document);

            var replacementFunctionType =
                DetermineReplacementFunctionType(semanticModel, invocationOperation);

            var replacementFunctionParameterName = MakeFirstLetterSmall(invokedMethodIdentifierSyntax.Identifier.Text);

            var replacementFunctionParameter = (ParameterSyntax) syntaxGenerator.ParameterDeclaration(
                replacementFunctionParameterName,
                syntaxGenerator.TypeExpression(replacementFunctionType));

            var containingMethodParameterList = containingMethod.ParameterList;

            var updatedContainingMethodParameterList =
                containingMethodParameterList.AddParameters(replacementFunctionParameter);

            var parameterListChange = new NodeChange(containingMethodParameterList, updatedContainingMethodParameterList);
            return (parameterListChange, replacementFunctionParameterName);
        }

        private static INamedTypeSymbol DetermineReplacementFunctionType(
            SemanticModel semanticModel,
            IInvocationOperation invocationOperation)
        {
            var invokedMethodParameterTypes = invocationOperation.TargetMethod.Parameters.Select(x => x.Type).ToArray();

            INamedTypeSymbol replacementFunctionType;

            if (invocationOperation.TargetMethod.ReturnsVoid)
            {
                replacementFunctionType =
                    semanticModel.Compilation.GetTypeByMetadataName(ActionTypes[invokedMethodParameterTypes.Length].FullName);

                if (invokedMethodParameterTypes.Length > 0)
                {
                    replacementFunctionType = replacementFunctionType.Construct(invokedMethodParameterTypes);
                }
            }
            else
            {
                replacementFunctionType =
                    semanticModel.Compilation.GetTypeByMetadataName(FunctionTypes[invokedMethodParameterTypes.Length].FullName);

                replacementFunctionType = replacementFunctionType.Construct(invokedMethodParameterTypes
                    .Concat(new[] {invocationOperation.TargetMethod.ReturnType}).ToArray());
            }

            return replacementFunctionType;
        }

        public static string MakeFirstLetterSmall(string str)
        {
            if (str.Length == 0)
                return str;

            return char.ToLower(str[0]) + str.Substring(1);
        }
    }
}
