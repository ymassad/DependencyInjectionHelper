using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DependencyInjectionHelper
{
    [CreateMatchMethods(typeof(InvocationOrObjectCreation))]
    public static class InvocationOrObjectCreationExtensionMethods
    {
        public static TResult Match<TResult>(this InvocationOrObjectCreation instance, System.Func<InvocationOrObjectCreation.Invocation, TResult> invocationCase, System.Func<InvocationOrObjectCreation.ObjectCreation, TResult> objectCreationCase)
        {
            if (instance is InvocationOrObjectCreation.Invocation invocation)
                return invocationCase(invocation);
            if (instance is InvocationOrObjectCreation.ObjectCreation objectCreation)
                return objectCreationCase(objectCreation);
            throw new System.Exception("Invalid InvocationOrObjectCreation type");
        }

        public static void Match(this InvocationOrObjectCreation instance, System.Action<InvocationOrObjectCreation.Invocation> invocationCase, System.Action<InvocationOrObjectCreation.ObjectCreation> objectCreationCase)
        {
            if (instance is InvocationOrObjectCreation.Invocation invocation)
            {
                invocationCase(invocation);
                return;
            }

            if (instance is InvocationOrObjectCreation.ObjectCreation objectCreation)
            {
                objectCreationCase(objectCreation);
                return;
            }

            throw new System.Exception("Invalid InvocationOrObjectCreation type");
        }

        public static ArgumentListSyntax GetArgumentList(this InvocationOrObjectCreation instance)
        {
            return instance.Match(inv => inv.Syntax.ArgumentList, objectCreation => objectCreation.Syntax.ArgumentList);
        }

        public static SyntaxNode GetSyntax(this InvocationOrObjectCreation instance)
        {
            return instance.Match<SyntaxNode>(inv => inv.Syntax, objectCreation => objectCreation.Syntax);
        }
    }
}