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

        public static Func<ImmutableArray<Argument>, Maybe<ImmutableArray<WhatToDoWithArgument>>> WhatToDoWithArguments;

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

        public static Maybe<InvocationOrObjectCreation> GetInvocationOrObjectCreation(IdentifierNameSyntax node)
        {
            switch (node.Parent)
            {
                case InvocationExpressionSyntax invocation:
                    return new InvocationOrObjectCreation.Invocation(invocation);
                case ObjectCreationExpressionSyntax objectCreation:
                    return new InvocationOrObjectCreation.ObjectCreation(objectCreation);
                case MemberAccessExpressionSyntax memberAccess when memberAccess.Parent is InvocationExpressionSyntax invocation:
                    return new InvocationOrObjectCreation.Invocation(invocation);
                default:
                    return Maybe.NoValue;
            }
        }


        public sealed override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);

            var semanticModel = await context.Document.GetSemanticModelAsync().ConfigureAwait(false);

            var node = root.FindNode(context.Span);

            if (!(node is IdentifierNameSyntax nameSyntax))
                return;

            var invocationSyntax = GetInvocation(nameSyntax);

            if (invocationSyntax.HasNoValue)
                return;


            var invocationOperation = semanticModel.GetOperation(invocationSyntax.GetValue()) as IInvocationOperation;

            if (invocationOperation == null)
                return;

            var arguments = invocationOperation.Arguments.Select(x => x.Parameter)
                .Select(x => new Argument(x.Type, x.Name))
                .ToImmutableArray();

            var containingMethod = invocationSyntax.GetValue().Ancestors().OfType<BaseMethodDeclarationSyntax>().First();

            if (containingMethod is ConstructorDeclarationSyntax constructor &&
                semanticModel.GetDeclaredSymbol(constructor).IsStatic)
            {
                return;
            }

            var action = new MyCodeAction(
                "Extract as a dependency",
                (whatToDoWithArgs, c) => ExtractDependency(
                    context.Document,
                    invocationSyntax.GetValue(),
                    nameSyntax,
                    semanticModel,
                    c, invocationOperation, whatToDoWithArgs,
                    containingMethod),
                () => WhatToDoWithArguments(arguments));

            context.RegisterRefactoring(action);
        }

        private async Task<Solution> ExtractDependency(Document document,
            InvocationExpressionSyntax invocationSyntax,
            IdentifierNameSyntax invokedMethodIdentifierSyntax,
            SemanticModel semanticModel,
            CancellationToken cancellationToken, IInvocationOperation invocationOperation,
            Maybe<ImmutableArray<WhatToDoWithArgument>> whatToDoWithArgsMaybe,
            BaseMethodDeclarationSyntax containingMethod)
        {
            var solution = document.Project.Solution;

            if (whatToDoWithArgsMaybe.HasNoValue)
                return solution;

            var whatToDoWithArgs = whatToDoWithArgsMaybe.GetValue();

            var documentRoot = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

    
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
                    whatToDoWithArgs,
                    invocationSyntax);

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
                parametersToRemove,
                invocationSyntax).ConfigureAwait(false);

            foreach (var changeToCallers in changesToCallers)
            {
                AddNewChangeToDocument(changeToCallers.document, changeToCallers.change);
            }

            return await UpdateSolution(cancellationToken, solution, nodesToReplace).ConfigureAwait(false);
        }

        private static (NodeChange parameterListChange, string replacementFunctionParameterName,
            ImmutableArray<IParameterSymbol> parametersToRemove) GetChangeToContainingMethodParameters(
                Document document,
                IdentifierNameSyntax invokedMethodIdentifierSyntax,
                SemanticModel semanticModel,
                ImmutableArray<(IArgumentOperation arg, WhatToDoWithArgument whatTodo)> argsAndWhatToDoWithThem,
                BaseMethodDeclarationSyntax containingMethod,
                SyntaxNode documentRoot,
                IInvocationOperation invocationOperation,
                ImmutableArray<WhatToDoWithArgument> whatToDoWithArgs,
                InvocationExpressionSyntax invocationSyntax)
        {
            var parametersUsedInArgumentsToRemoveAndInInvokedExpression =
                argsAndWhatToDoWithThem.Where(x => x.whatTodo == WhatToDoWithArgument.Remove)
                    .Select(x => x.arg.Syntax)
                    .Concat(new []{ invocationSyntax.Expression })
                    .SelectMany(x => x.DescendantNodes().OfType<IdentifierNameSyntax>())
                    .Select(x => semanticModel.GetSymbolInfo(x).Symbol)
                    .OfType<IParameterSymbol>()
                    .Distinct()
                    .ToList();

            var argumentsToRemove =
                argsAndWhatToDoWithThem.Where(x => x.whatTodo == WhatToDoWithArgument.Remove)
                    .Select(x => x.arg.Syntax)
                    .ToList();

            var identifierNameNodesOutSideOfArgumentsToRemoveAndOutsideOfInvokedExpressionThatRepresentParameters =
                containingMethod.DescendantNodes()
                    .OfType<IdentifierNameSyntax>()
                    .Where(x => !argumentsToRemove.Any(argToRemove => argToRemove.Contains(x)) && !invocationSyntax.Expression.Contains(x))
                    .Select(x => (node: x, parameter: semanticModel.GetSymbolInfo(x).Symbol as IParameterSymbol))
                    .Where(x => !(x.parameter is null))
                    .ToList();

            List<IParameterSymbol> parametersToRemove = new List<IParameterSymbol>();

            foreach (var param in parametersUsedInArgumentsToRemoveAndInInvokedExpression)
            {
                var anyUsageOutsideOfArgumentsToRemove =
                    identifierNameNodesOutSideOfArgumentsToRemoveAndOutsideOfInvokedExpressionThatRepresentParameters.Any(x =>
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

            foreach (var parameterSyntaxToRemove in parameterSyntaxesToRemove.Select(x => parameters.IndexOf(x)).OrderByDescending(x => x).ToList())
            {
                parameters = parameters.RemoveAt(parameterSyntaxToRemove);
            }

            parameters = parameters.Add(replacementFunctionParameter);

            var updatedContainingMethodParameterList =
                containingMethodParameterList.WithParameters(parameters);

            var parameterListChange = new NodeChange(containingMethodParameterList, updatedContainingMethodParameterList);

            return (parameterListChange, replacementFunctionParameterName, parametersToRemove.ToImmutableArray());
        }

        private static NodeChange GetMethodInvocationChange(
            InvocationExpressionSyntax invocationSyntax,
            IdentifierNameSyntax invokedMethodIdentifierSyntax,
            ImmutableArray<(IArgumentOperation arg, WhatToDoWithArgument whatTodo)> argsAndWhatToDoWithThem,
            string replacementFunctionParameterName)
        {
            var indexesOfArgumentsToRemove =
                argsAndWhatToDoWithThem.Where(x => x.whatTodo == WhatToDoWithArgument.Remove)
                    .Select(x => x.arg)
                    .Select(x => invocationSyntax.ArgumentList.Arguments.IndexOf((ArgumentSyntax) x.Syntax))
                    .OrderByDescending(x => x)
                    .ToList();

            var updatedArguments = invocationSyntax.ArgumentList.Arguments;

            foreach (var index in indexesOfArgumentsToRemove)
            {
                updatedArguments = updatedArguments.RemoveAt(index);
            }

            var updatedInvocationSyntax =
                invocationSyntax
                    .ReplaceNode(invocationSyntax.Expression,
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
                var root = await doc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

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
            BaseMethodDeclarationSyntax methodContainingCallToExtract,
            Solution solution,
            IMethodSymbol invokedMethod,
            ImmutableArray<(IArgumentOperation arg, WhatToDoWithArgument whatTodo)> argsAndWhatToDoWithThem,
            ImmutableArray<IParameterSymbol> parametersToRemove,
            InvocationExpressionSyntax invocationSyntaxInCallee)
        {
            List<(Document document, NodeChange change)> changes = new List<(Document document, NodeChange change)>();

            var usagesOfContainingMethod =
                (await SymbolFinder.FindReferencesAsync(semanticModel.GetDeclaredSymbol(methodContainingCallToExtract), solution,
                    cancellationToken).ConfigureAwait(false)).ToList();


            foreach (var reference in usagesOfContainingMethod.SelectMany(x => x.Locations))
            {
                var refDocument = reference.Document;

                var refDocumentRoot = await refDocument.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

                var refNode = refDocumentRoot.FindNode(reference.Location.SourceSpan);

                if (!(refNode is IdentifierNameSyntax refNameSyntax))
                    continue;

                var inv = GetInvocationOrObjectCreation(refNameSyntax);

                if (inv.HasNoValue)
                    continue;

                var refInvocation = inv.GetValue();

                var oldArgumentList = refInvocation.GetArgumentList();

                var syntaxGenerator = SyntaxGenerator.GetGenerator(refDocument);

                var operation = semanticModel.GetOperation(refInvocation.GetSyntax());

                var invocationArgumentOperations =
                    refInvocation.Match(
                        invocationCase: _ => ((IInvocationOperation) operation).Arguments,
                        objectCreationCase: _ => ((IObjectCreationOperation) operation).Arguments);

                var invokedMethodName = invokedMethod.Name;

                var parametersOfMethodContainingCallToExtract = methodContainingCallToExtract.ParameterList
                    .Parameters
                    .Select(x => semanticModel.GetDeclaredSymbol(x))
                    .ToList();

                var argumentsForParameters =
                    parametersOfMethodContainingCallToExtract
                        .ToDictionary(x => x,
                            x => (ArgumentSyntax)invocationArgumentOperations.Single(a => a.Parameter.Equals(x)).Syntax);

                Maybe<ExpressionSyntax> expressionToUseInCaller = default;

                if (invocationSyntaxInCallee.Expression is MemberAccessExpressionSyntax memberAccess)
                {
                    expressionToUseInCaller =
                        ReplaceUsageOfParametersWithCorrespondingArguments(
                            semanticModel,
                            memberAccess.Expression,
                            argumentsForParameters); 
                }

                var expressionToInvoke =
                    expressionToUseInCaller.Match(
                        exp => (ExpressionSyntax) syntaxGenerator.MemberAccessExpression(exp, invokedMethodName),
                        () => SyntaxFactory.IdentifierName(invokedMethodName));

                LambdaExpressionSyntax CreateLambdaExpression()
                {
                    var lambdaParameters = 
                        argsAndWhatToDoWithThem.Where(x => x.whatTodo == WhatToDoWithArgument.Keep)
                            .Select(x => x.arg.Parameter.Name)
                            .Select(x => syntaxGenerator.LambdaParameter(x))
                            .ToList();

                    var invocationExpression = syntaxGenerator.InvocationExpression(
                        expressionToInvoke,
                        argsAndWhatToDoWithThem
                            .Select(x =>
                            {
                                if (x.whatTodo == WhatToDoWithArgument.Remove)
                                {
                                    var argumentSyntax = (ArgumentSyntax)x.arg.Syntax;

                                    var updatedArgumentExpression =
                                        ReplaceUsageOfParametersWithCorrespondingArguments(semanticModel, argumentSyntax.Expression, argumentsForParameters);

                                    return syntaxGenerator.Argument(updatedArgumentExpression);
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
                    invocationArgumentOperations.Where(x => parametersToRemove.Contains(x.Parameter))
                        .Select(x => (ArgumentSyntax) x.Syntax);

                foreach (var argToRemove in argumentsToRemove.Select(x => arguments.IndexOf(x)).OrderByDescending(x => x).ToList())
                {
                    arguments = arguments.RemoveAt(argToRemove);
                }

                arguments = arguments.Add(SyntaxFactory.Argument(CreateLambdaExpression()));

                var newArgumentList =
                    oldArgumentList.WithArguments(arguments);
                        

                changes.Add((refDocument, new NodeChange(oldArgumentList, newArgumentList)));
            }

            return changes;
        }

        private static ExpressionSyntax ReplaceUsageOfParametersWithCorrespondingArguments(
            SemanticModel semanticModel,
            ExpressionSyntax expression,
            Dictionary<IParameterSymbol, ArgumentSyntax> argumentsForParameters)
        {
            var nodesToReplace =
                expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                    .Select(x => (node: x, param: semanticModel.GetSymbolInfo(x).Symbol as IParameterSymbol))
                    .Where(x => x.param != null && argumentsForParameters.ContainsKey(x.param))
                    .ToDictionary(x => x.node, x => argumentsForParameters[x.param].Expression);

            return expression.ReplaceNodes(nodesToReplace.Keys, (x, _) => nodesToReplace[x]);
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
