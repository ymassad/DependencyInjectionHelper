using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DependencyInjectionHelper
{
    public abstract class InvocationOrObjectCreation
    {
        private InvocationOrObjectCreation()
        {
        }

        public sealed class Invocation : InvocationOrObjectCreation
        {
            public Invocation(InvocationExpressionSyntax syntax)
            {
                Syntax = syntax;
            }

            public InvocationExpressionSyntax Syntax { get; }
        }

        public sealed class ObjectCreation : InvocationOrObjectCreation
        {
            public ObjectCreation(ObjectCreationExpressionSyntax syntax)
            {
                Syntax = syntax;
            }

            public ObjectCreationExpressionSyntax Syntax { get; }
        }
    }


}