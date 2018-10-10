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

        public static Func<ImmutableArray<Parameter>, ImmutableArray<WhatToDoWithParameter>> WhatToDoWithArguments;

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
            WhatToDoWithArguments =
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

            var whatToDoWithArgs =
                WhatToDoWithArguments(
                    invocationOperation.Arguments.Select(x => x.Parameter)
                        .Select(x => new Parameter(x.Type, x.Name))
                        .ToImmutableArray());

            var (parameterListChange , replacementFunctionParameterName) =
                UpdateParameterListToContainNewDependency(
                    document,
                    invokedMethodIdentifierSyntax,
                    semanticModel,
                    invocationOperation,
                    containingMethod,
                    whatToDoWithArgs);


            var nodesToReplace =
                new Dictionary<Document, List<NodeChange>>();

            void AddNewChangeToDocument(Document doc, NodeChange change)
            {
                nodesToReplace.GetOrAdd(doc, () => new List<NodeChange>()).Add(change);
            }

            AddNewChangeToDocument(document, new NodeChange(parameterListChange.OldNode, parameterListChange.NewNode));


            var argsAndWhatToDoWithThem =
                invocationOperation.Arguments
                    .Zip(whatToDoWithArgs, (arg, whatTodo) => (arg, whatTodo))
                    .ToList();

            var updateArguments =
                argsAndWhatToDoWithThem.Where(x => x.whatTodo == WhatToDoWithParameter.Remove)
                    .Select(x => x.arg)
                    .Aggregate(invocationSyntax.ArgumentList.Arguments,
                        (args, arg) => args.Remove((ArgumentSyntax) arg.Syntax));


            var updatedInvocationSyntax =
                invocationSyntax
                    .ReplaceNode(invokedMethodIdentifierSyntax,
                        invokedMethodIdentifierSyntax.WithIdentifier(
                            SyntaxFactory.Identifier(replacementFunctionParameterName)))
                    .WithArgumentList(SyntaxFactory.ArgumentList(updateArguments));
                    
                    

            AddNewChangeToDocument(document, new NodeChange(invocationSyntax, updatedInvocationSyntax));




            var changesToCallers = await GetChangesToCallers(
                semanticModel, cancellationToken, containingMethod, solution, invocationOperation.TargetMethod, whatToDoWithArgs);

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
            IMethodSymbol invokedMethod,
            ImmutableArray<WhatToDoWithParameter> whatToDoWithArgs)
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

                var anyArgumentsToRemove = whatToDoWithArgs.Any(x => x == WhatToDoWithParameter.Remove);

                var syntaxGenerator = SyntaxGenerator.GetGenerator(refDocument);

                var refInvocationOperation = (IInvocationOperation) semanticModel.GetOperation(refInvocation);

                var invokedMethodName = invokedMethod.Name;


                //object Get123()
                //{
                //    if (invokedMethod.ReturnsVoid)
                //    {
                //        return syntaxGenerator.VoidReturningLambdaExpression(
                //            Enumerable.Empty<SyntaxNode>(),
                //            syntaxGenerator.InvocationExpression(
                //                SyntaxFactory.IdentifierName(invokedMethodName), ))
                //    }
                //}

                var newArgumentList =
                    //anyArgumentsToRemove
                    //? syntaxGenerator.lambda

                    //: oldArgumentList.AddArguments(
                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName(invokedMethodName));

                localChanges.Add((refDocument, new NodeChange(oldArgumentList, newArgumentList)));
            }

            return localChanges;
        }

        private static (NodeChange change, string replacementFunctionParameterName)
            UpdateParameterListToContainNewDependency(Document document,
                IdentifierNameSyntax invokedMethodIdentifierSyntax,
                SemanticModel semanticModel,
                IInvocationOperation invocationOperation,
                MethodDeclarationSyntax containingMethod,
                ImmutableArray<WhatToDoWithParameter> whatToDoWithArgs)
        {
            var syntaxGenerator = SyntaxGenerator.GetGenerator(document);

            var replacementFunctionType =
                DetermineReplacementFunctionType(semanticModel, invocationOperation, whatToDoWithArgs);

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
            IInvocationOperation invocationOperation,
            ImmutableArray<WhatToDoWithParameter> whatToDoWithArgs)
        {
            var typesOfParametersToKeep =
                invocationOperation.Arguments.Select(x => x.Parameter)
                    .Zip(whatToDoWithArgs, (param, whatToDo) => (param, whatToDo))
                    .Where(x => x.whatToDo == WhatToDoWithParameter.Keep)
                    .Select(x => x.param.Type).ToArray();

            INamedTypeSymbol replacementFunctionType;

            if (invocationOperation.TargetMethod.ReturnsVoid)
            {
                replacementFunctionType =
                    semanticModel.Compilation.GetTypeByMetadataName(ActionTypes[typesOfParametersToKeep.Length].FullName);

                if (typesOfParametersToKeep.Length > 0)
                {
                    replacementFunctionType = replacementFunctionType.Construct(typesOfParametersToKeep);
                }
            }
            else
            {
                replacementFunctionType =
                    semanticModel.Compilation.GetTypeByMetadataName(FunctionTypes[typesOfParametersToKeep.Length].FullName);

                replacementFunctionType = replacementFunctionType.Construct(typesOfParametersToKeep
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
