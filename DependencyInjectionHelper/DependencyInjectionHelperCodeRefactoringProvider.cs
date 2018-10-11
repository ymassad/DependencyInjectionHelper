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

        public static Func<ImmutableArray<Argument>, ImmutableArray<WhatToDoWithArgument>> WhatToDoWithArguments;

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
                x => x.Select(_ => WhatToDoWithArgument.Keep)
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

            var documentRoot = await document.GetSyntaxRootAsync(cancellationToken);

            var containingMethod = invocationSyntax.Ancestors().OfType<MethodDeclarationSyntax>().First();

            var invocationOperation = semanticModel.GetOperation(invocationSyntax) as IInvocationOperation;

            if (invocationOperation == null)
                return solution;

            var whatToDoWithArgs =
                WhatToDoWithArguments(
                    invocationOperation.Arguments.Select(x => x.Parameter)
                        .Select(x => new Argument(x.Type, x.Name))
                        .ToImmutableArray());

            var nodesToReplace =
                new Dictionary<Document, List<NodeChange>>();

            void AddNewChangeToDocument(Document doc, NodeChange change)
            {
                nodesToReplace.GetOrAdd(doc, () => new List<NodeChange>()).Add(change);
            }


            var argsAndWhatToDoWithThem =
                invocationOperation.Arguments
                    .Zip(whatToDoWithArgs, (arg, whatTodo) => (arg, whatTodo))
                    .ToImmutableArray();

            var (parameterListChange, replacementFunctionParameterName, parametersToRemove) =
                GetChangeToContainingMethodParameters(
                    document,
                    invokedMethodIdentifierSyntax,
                    semanticModel,
                    argsAndWhatToDoWithThem,
                    containingMethod,
                    documentRoot,
                    invocationOperation,
                    whatToDoWithArgs);

            AddNewChangeToDocument(
                document,
                parameterListChange);

            var nodeChange =
                GetMethodInvocationChange(
                    invocationSyntax,
                    invokedMethodIdentifierSyntax,
                    argsAndWhatToDoWithThem,
                    replacementFunctionParameterName);

            AddNewChangeToDocument(document, nodeChange);

            var changesToCallers = await GetChangesToCallers(
                semanticModel,
                cancellationToken,
                containingMethod,
                solution,
                invocationOperation.TargetMethod,
                argsAndWhatToDoWithThem,
                parametersToRemove);

            foreach (var changeToCallers in changesToCallers)
            {
                AddNewChangeToDocument(changeToCallers.document, changeToCallers.change);
            }

            return await UpdateSolution(cancellationToken, solution, nodesToReplace);
        }

        private static (NodeChange parameterListChange, string replacementFunctionParameterName, ImmutableArray<IParameterSymbol> parametersToRemove) GetChangeToContainingMethodParameters(
            Document document,
            IdentifierNameSyntax invokedMethodIdentifierSyntax,
            SemanticModel semanticModel,
            ImmutableArray<(IArgumentOperation arg, WhatToDoWithArgument whatTodo)> argsAndWhatToDoWithThem,
            MethodDeclarationSyntax containingMethod,
            SyntaxNode documentRoot,
            IInvocationOperation invocationOperation,
            ImmutableArray<WhatToDoWithArgument> whatToDoWithArgs)
        {
            var parametersUsedInArgumentsToRemove =
                argsAndWhatToDoWithThem.Where(x => x.whatTodo == WhatToDoWithArgument.Remove)
                    .SelectMany(x => x.arg.Syntax.DescendantNodes().OfType<IdentifierNameSyntax>())
                    .Select(x => semanticModel.GetSymbolInfo(x).Symbol)
                    .OfType<IParameterSymbol>()
                    .Distinct()
                    .ToList();

            var argumentsToRemove =
                argsAndWhatToDoWithThem.Where(x => x.whatTodo == WhatToDoWithArgument.Remove)
                    .Select(x => x.arg.Syntax)
                    .ToList();

            var identifierNameNodesOutSideOfArgumentsToRemoveThatRepresentParameters =
                containingMethod.DescendantNodes()
                    .OfType<IdentifierNameSyntax>()
                    .Where(x => !argumentsToRemove.Any(argToRemove => argToRemove.Contains(x)))
                    .Select(x => (node: x, parameter: semanticModel.GetSymbolInfo(x).Symbol as IParameterSymbol))
                    .Where(x => !(x.parameter is null))
                    .ToList();

            List<IParameterSymbol> parametersToRemove = new List<IParameterSymbol>();

            foreach (var param in parametersUsedInArgumentsToRemove)
            {
                var anyUsageOutsideOfArgumentsToRemove =
                    identifierNameNodesOutSideOfArgumentsToRemoveThatRepresentParameters.Any(x =>
                        x.parameter.Equals(param));

                if (!anyUsageOutsideOfArgumentsToRemove)
                {
                    parametersToRemove.Add(param);
                }
            }

            var parameterSyntaxesToRemove = parametersToRemove.Select(x => x.Locations.Single())
                .Select(location => (ParameterSyntax) documentRoot.FindNode(location.SourceSpan))
                .ToList();

            var replacementFunctionParameter =
                DetermineNewReplacementFunctionParameter(
                    document, invokedMethodIdentifierSyntax, semanticModel, invocationOperation, whatToDoWithArgs);

            var replacementFunctionParameterName = replacementFunctionParameter.Identifier.Text;

            var containingMethodParameterList = containingMethod.ParameterList;

            var parameters = containingMethodParameterList.Parameters;

            foreach (var parameterSyntaxToRemove in parameterSyntaxesToRemove)
            {
                parameters = parameters.Remove(parameterSyntaxToRemove);
            }

            parameters = parameters.Add(replacementFunctionParameter);

            var updatedContainingMethodParameterList =
                containingMethodParameterList.WithParameters(parameters);

            var parameterListChange = new NodeChange(containingMethodParameterList, updatedContainingMethodParameterList);

            return (parameterListChange, replacementFunctionParameterName, parametersToRemove.ToImmutableArray());
        }

        private static NodeChange GetMethodInvocationChange(InvocationExpressionSyntax invocationSyntax,
            IdentifierNameSyntax invokedMethodIdentifierSyntax, ImmutableArray<(IArgumentOperation arg, WhatToDoWithArgument whatTodo)> argsAndWhatToDoWithThem,
            string replacementFunctionParameterName)
        {
            var updatedArguments =
                argsAndWhatToDoWithThem.Where(x => x.whatTodo == WhatToDoWithArgument.Remove)
                    .Select(x => x.arg)
                    .Aggregate(invocationSyntax.ArgumentList.Arguments,
                        (args, arg) => args.Remove((ArgumentSyntax) arg.Syntax));

            var updatedInvocationSyntax =
                invocationSyntax
                    .ReplaceNode(invokedMethodIdentifierSyntax,
                        invokedMethodIdentifierSyntax.WithIdentifier(
                            SyntaxFactory.Identifier(replacementFunctionParameterName)))
                    .WithArgumentList(SyntaxFactory.ArgumentList(updatedArguments));

            return new NodeChange(invocationSyntax, updatedInvocationSyntax);
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
            ImmutableArray<(IArgumentOperation arg, WhatToDoWithArgument whatTodo)> argsAndWhatToDoWithThem,
            ImmutableArray<IParameterSymbol> parametersToRemove)
        {
            List<(Document document, NodeChange change)> changes = new List<(Document document, NodeChange change)>();

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

                var anyArgumentsToRemove = argsAndWhatToDoWithThem.Any(x => x.whatTodo == WhatToDoWithArgument.Remove);

                var syntaxGenerator = SyntaxGenerator.GetGenerator(refDocument);

                var refInvocationOperation = (IInvocationOperation) semanticModel.GetOperation(refInvocation);

                var invokedMethodName = invokedMethod.Name;

                LambdaExpressionSyntax CreateLambdaExpression()
                {

                    var lambdaParameters = 
                        argsAndWhatToDoWithThem.Where(x => x.whatTodo == WhatToDoWithArgument.Keep)
                            .Select(x => x.arg.Parameter.Name)
                            .Select(x => syntaxGenerator.LambdaParameter(x))
                            .ToList();

                    var invocationExpression = syntaxGenerator.InvocationExpression(
                        SyntaxFactory.IdentifierName(invokedMethodName),
                        argsAndWhatToDoWithThem
                            .Select(x =>
                            {
                                if (x.whatTodo == WhatToDoWithArgument.Remove)
                                {
                                    var argumentSyntax = (ArgumentSyntax)x.arg.Syntax;

                                    if (semanticModel.GetSymbolInfo(argumentSyntax.Expression).Symbol is
                                        IParameterSymbol parameter)
                                    {
                                        var callerArgumentForParameter = refInvocationOperation.Arguments
                                            .Where(a => a.Parameter.Equals(parameter)).Select(a => a.Syntax).Single();

                                        return callerArgumentForParameter;
                                    }

                                    return argumentSyntax;
                                }
                                else //Keep
                                {
                                    return syntaxGenerator.IdentifierName(x.arg.Parameter.Name);
                                }
                            }));

                    if (invokedMethod.ReturnsVoid)
                    {
                        return (LambdaExpressionSyntax) syntaxGenerator.VoidReturningLambdaExpression(
                            lambdaParameters,
                            invocationExpression);
                    }
                    else
                    {
                        return (LambdaExpressionSyntax)syntaxGenerator.ValueReturningLambdaExpression(
                            lambdaParameters,
                            invocationExpression);
                    }
                }

                var arguments = oldArgumentList.Arguments;

                var argumentsToRemove =
                    refInvocationOperation.Arguments.Where(x => parametersToRemove.Contains(x.Parameter))
                        .Select(x => (ArgumentSyntax) x.Syntax);

                foreach (var arg in argumentsToRemove)
                    arguments = arguments.Remove(arg);

                arguments = arguments.Add(SyntaxFactory.Argument(
                    anyArgumentsToRemove
                        ? (ExpressionSyntax) CreateLambdaExpression()
                        : SyntaxFactory.IdentifierName(invokedMethodName)));

                var newArgumentList =
                    oldArgumentList.WithArguments(arguments);
                        

                changes.Add((refDocument, new NodeChange(oldArgumentList, newArgumentList)));
            }

            return changes;
        }

  

        private static ParameterSyntax DetermineNewReplacementFunctionParameter(Document document,
            IdentifierNameSyntax invokedMethodIdentifierSyntax, SemanticModel semanticModel,
            IInvocationOperation invocationOperation, ImmutableArray<WhatToDoWithArgument> whatToDoWithArgs)
        {
            var syntaxGenerator = SyntaxGenerator.GetGenerator(document);

            var replacementFunctionType =
                DetermineReplacementFunctionType(semanticModel, invocationOperation, whatToDoWithArgs);

            var replacementFunctionParameterName = MakeFirstLetterSmall(invokedMethodIdentifierSyntax.Identifier.Text);

            return (ParameterSyntax) syntaxGenerator.ParameterDeclaration(
                replacementFunctionParameterName,
                syntaxGenerator.TypeExpression(replacementFunctionType));
        }

        private static INamedTypeSymbol DetermineReplacementFunctionType(
            SemanticModel semanticModel,
            IInvocationOperation invocationOperation,
            ImmutableArray<WhatToDoWithArgument> whatToDoWithArgs)
        {
            var typesOfParametersToKeep =
                invocationOperation.Arguments.Select(x => x.Parameter)
                    .Zip(whatToDoWithArgs, (param, whatToDo) => (param, whatToDo))
                    .Where(x => x.whatToDo == WhatToDoWithArgument.Keep)
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
