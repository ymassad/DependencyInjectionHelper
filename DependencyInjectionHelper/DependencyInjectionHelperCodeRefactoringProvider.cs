using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
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
    public class Parameter
    {
        public Parameter(ITypeSymbol type, string name)
        {
            Type = type;
            Name = name;
        }

        public ITypeSymbol Type { get; }

        public string Name { get; }
    }

    public enum WhatToDoWithParameter
    {
        Keep,
        Remove
    }


    [ExportCodeRefactoringProvider(LanguageNames.CSharp, Name = nameof(DependencyInjectionHelperCodeRefactoringProvider)), Shared]
    public class DependencyInjectionHelperCodeRefactoringProvider : CodeRefactoringProvider
    {

        public static Func<ImmutableArray<Parameter>, ImmutableArray<WhatToDoWithParameter>> WhatToDoWithParameters;

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

            InvocationExpressionSyntax invocation = null;

            if (!(node is IdentifierNameSyntax nameSynax))
                return;

            var inv = GetInvocation(nameSynax);

            if (inv.HasNoValue)
                return;


            var action = new MyCodeAction(
                "Extract as a dependency",
                c => ExtractDependency(context.Document, inv.GetValue(), nameSynax, semanticModel, root, c));

            context.RegisterRefactoring(action);
        }

        private async Task<Solution> ExtractDependency(Document document,
            InvocationExpressionSyntax invocation,
            IdentifierNameSyntax nameSyntax,
            SemanticModel semanticModel,
            SyntaxNode root,
            CancellationToken cancellationToken)
        {
            //// Produce a reversed version of the type declaration's identifier token.
            //var identifierToken = typeDecl.Identifier;
            //var newName = new string(identifierToken.Text.ToCharArray().Reverse().ToArray());

            //// Get the symbol representing the type to be renamed.
            //var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            //var typeSymbol = semanticModel.GetDeclaredSymbol(typeDecl, cancellationToken);

            var method = invocation.Ancestors().OfType<MethodDeclarationSyntax>().First();

            var invocationOperation = semanticModel.GetOperation(invocation) as IInvocationOperation;

            var generator = SyntaxGenerator.GetGenerator(document);

            var paramTypes = invocationOperation.TargetMethod.Parameters.Select(x => x.Type).ToArray();

            var actionTypes = new Dictionary<int, Type>
            {
                [0] = typeof(Action),
                [1] = typeof(Action<>),
                [2] = typeof(Action<,>),
                [3] = typeof(Action<,,>),
                [4] = typeof(Action<,,,>),
                [5] = typeof(Action<,,,,>),
                [6] = typeof(Action<,,,,,>),
            };



            var functionTypes = new Dictionary<int, Type>
            {
                [0] = typeof(Func<>),
                [1] = typeof(Func<,>),
                [2] = typeof(Func<,,>),
                [3] = typeof(Func<,,,>),
                [4] = typeof(Func<,,,,>),
                [5] = typeof(Func<,,,,,>),
                [6] = typeof(Func<,,,,,,>)
            };

            INamedTypeSymbol functionType;
            if (invocationOperation.TargetMethod.ReturnsVoid)
            {
                functionType = semanticModel.Compilation.GetTypeByMetadataName(actionTypes[paramTypes.Length].FullName);

                if (paramTypes.Length > 0)
                {
                    functionType = functionType.Construct(paramTypes);
                }


            }
            else
            {
                functionType = semanticModel.Compilation.GetTypeByMetadataName(functionTypes[paramTypes.Length].FullName);

 
                functionType = functionType.Construct(paramTypes.Concat(new []{invocationOperation.TargetMethod.ReturnType}).ToArray());
                
            }


            var functionName = MakeFirstLetterSmall(nameSyntax.Identifier.Text);
            var param = (ParameterSyntax)generator.ParameterDeclaration(functionName,
                generator.TypeExpression(functionType));

            var parameterListParameters = method.ParameterList;

            var newList = parameterListParameters.AddParameters(param);


            var solution = document.Project.Solution;

            var references = (await SymbolFinder.FindReferencesAsync(semanticModel.GetDeclaredSymbol(method), solution, cancellationToken)).ToList();

            var invokedMethod = invocationOperation.TargetMethod.Name;

            Dictionary<Document, SyntaxNode> documentRoots = new Dictionary<Document, SyntaxNode>();

            Dictionary<Document, List<(SyntaxNode oldNode, SyntaxNode newNode)>> nodesToReplace =
                new Dictionary<Document, List<(SyntaxNode oldNode, SyntaxNode newNode)>>();

            foreach (var reference in references.SelectMany(x => x.Locations))
            {
                var refDocument = reference.Document;

                var refDocumentRoot = await refDocument.GetSyntaxRootAsync(cancellationToken);

                var a = refDocumentRoot.FindNode(reference.Location.SourceSpan);

                if(!(a is IdentifierNameSyntax refNameSyntax))
                    continue;
                ;

                var inv = GetInvocation(refNameSyntax);

                if(inv.HasNoValue)
                    continue;

                var refInvocation = inv.GetValue();

                var oldArgumentList = refInvocation.ArgumentList;
                var newArgumentList = oldArgumentList.AddArguments(
                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName(invokedMethod)));

                var list1 = nodesToReplace.GetOrAdd(refDocument, () => new List<(SyntaxNode oldNode, SyntaxNode newNode)>());

                list1.Add((oldArgumentList, newArgumentList));

                documentRoots.AddIfNotExists(refDocument, () => refDocumentRoot);

                int aaa = 0;
            }

            var list = nodesToReplace.GetOrAdd(document, () => new List<(SyntaxNode oldNode, SyntaxNode newNode)>());

            list.Add((parameterListParameters, newList));
            list.Add((nameSyntax, nameSyntax.WithIdentifier(SyntaxFactory.Identifier(functionName))));

            documentRoots.AddIfNotExists(document, () => root);

            Solution newSolution = solution;

            foreach (var doc in nodesToReplace.Keys)
            {
                var droot = documentRoots[doc];

                var newdroot = droot.ReplaceNodes(nodesToReplace[document].Select(x => x.oldNode), (x, _) =>
                    {
                        return nodesToReplace[document].Where(e => e.oldNode.Equals(x)).Select(e => e.newNode).Single();
                    });

                newSolution = newSolution.WithDocumentSyntaxRoot(doc.Id, newdroot);
            }



            

            return newSolution;


            //action1();
            // Produce a new solution that has all references to that type renamed, including the declaration.
            var originalSolution = solution;
            //var optionSet = originalSolution.Workspace.Options;
            //var newSolution = await Renamer.RenameSymbolAsync(document.Project.Solution, typeSymbol, newName, optionSet, cancellationToken).ConfigureAwait(false);

            // Return the new solution with the now-uppercase type name.
            return originalSolution;
        }

        public static string MakeFirstLetterSmall(string str)
        {
            if (str.Length == 0)
                return str;

            return char.ToLower(str[0]) + str.Substring(1);
        }
    }

    public static class Extensions
    {
        public static TValue GetOrAdd<TKey, TValue>(
            this Dictionary<TKey, TValue> dictionary, TKey key,
            Func<TValue> factory)
        {
            if (dictionary.ContainsKey(key))
                return dictionary[key];

            var value = factory();

            dictionary.Add(key, value);

            return value;
        }

        public static void AddIfNotExists<TKey, TValue>(
            this Dictionary<TKey, TValue> dictionary,
            TKey key,
            Func<TValue> factory)
        {
            if (dictionary.ContainsKey(key))
                return;

            dictionary.Add(key, factory());
        }
    }

    public class MyCodeAction : CodeAction
    {
        public override string Title { get; }

        private readonly Func<CancellationToken, Task<Solution>> func;

        public MyCodeAction(string title, Func<CancellationToken, Task<Solution>> func)
        {
            Title = title;
            this.func = func;
        }

        protected override Task<IEnumerable<CodeActionOperation>> ComputePreviewOperationsAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(Enumerable.Empty<CodeActionOperation>());
        }

        protected override Task<Solution> GetChangedSolutionAsync(CancellationToken cancellationToken)
        {
            return func(cancellationToken);
        }


    }
}
