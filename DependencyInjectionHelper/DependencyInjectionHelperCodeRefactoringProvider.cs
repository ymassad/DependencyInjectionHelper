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
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;




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

            if (node.Parent is InvocationExpressionSyntax)
            {
                return (InvocationExpressionSyntax)node.Parent;
            }
            else if (node.Parent is MemberAccessExpressionSyntax memberAccess)
            {
                if (memberAccess.Parent is InvocationExpressionSyntax)
                    return (InvocationExpressionSyntax)memberAccess.Parent;
            }

            return Maybe.NoValue;
        }

        public sealed override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);

            var semanticModel = await context.Document.GetSemanticModelAsync();

            var node = root.FindNode(context.Span);

            if (!(node is IdentifierNameSyntax nameSyntax))
                return;

            var inv = GetInvocation(nameSyntax);

            if (inv.HasNoValue)
                return;


            var action = new MyCodeAction(
                "Extract as a dependency",
                c => ExtractDependency(context.Document, root, inv.GetValue(), nameSyntax, semanticModel, c));

            context.RegisterRefactoring(action);
        }

        private async Task<Solution> ExtractDependency(Document document,
            SyntaxNode documentRoot,
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

            var syntaxGenerator = SyntaxGenerator.GetGenerator(document);

            var replacementFunctionType =
                DetermineReplacementFunctionType(semanticModel, invocationOperation);

            var replacementFunctionParameterName = MakeFirstLetterSmall(invokedMethodIdentifierSyntax.Identifier.Text);

            var param = (ParameterSyntax)syntaxGenerator.ParameterDeclaration(
                replacementFunctionParameterName,
                syntaxGenerator.TypeExpression(replacementFunctionType));

            var containingMethodParameterList = containingMethod.ParameterList;

            var updatedContainingMethodParameterList = containingMethodParameterList.AddParameters(param);

            var usagesOfContainingMethod = (await SymbolFinder.FindReferencesAsync(semanticModel.GetDeclaredSymbol(containingMethod), solution, cancellationToken)).ToList();

            var invokedMethod = invocationOperation.TargetMethod.Name;

            Dictionary<Document, List<(SyntaxNode oldNode, SyntaxNode newNode)>> nodesToReplace =
                new Dictionary<Document, List<(SyntaxNode oldNode, SyntaxNode newNode)>>();

            var list = nodesToReplace.GetOrAdd(document, () => new List<(SyntaxNode oldNode, SyntaxNode newNode)>());

            list.Add((containingMethodParameterList, updatedContainingMethodParameterList));
            list.Add((invokedMethodIdentifierSyntax, invokedMethodIdentifierSyntax.WithIdentifier(SyntaxFactory.Identifier(replacementFunctionParameterName))));

            foreach (var reference in usagesOfContainingMethod.SelectMany(x => x.Locations))
            {
                var refDocument = reference.Document;

                var refDocumentRoot = await refDocument.GetSyntaxRootAsync(cancellationToken);

                var refNode = refDocumentRoot.FindNode(reference.Location.SourceSpan);

                if(!(refNode is IdentifierNameSyntax refNameSyntax))
                    continue;

                var inv = GetInvocation(refNameSyntax);

                if(inv.HasNoValue)
                    continue;

                var refInvocation = inv.GetValue();

                var oldArgumentList = refInvocation.ArgumentList;
                var newArgumentList = oldArgumentList.AddArguments(
                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName(invokedMethod)));

                var list1 = nodesToReplace.GetOrAdd(refDocument, () => new List<(SyntaxNode oldNode, SyntaxNode newNode)>());

                list1.Add((oldArgumentList, newArgumentList));

            }



            Solution newSolution = solution;

            foreach (var doc in nodesToReplace.Keys)
            {
                var droot = await doc.GetSyntaxRootAsync(cancellationToken);

                var newdroot = droot.ReplaceNodes(nodesToReplace[document].Select(x => x.oldNode), (x, _) =>
                    {
                        return nodesToReplace[document].Where(e => e.oldNode.Equals(x)).Select(e => e.newNode).Single();
                    });

                newSolution = newSolution.WithDocumentSyntaxRoot(doc.Id, newdroot);
            }

            return newSolution;


        }

        private static INamedTypeSymbol DetermineReplacementFunctionType(SemanticModel semanticModel,
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
