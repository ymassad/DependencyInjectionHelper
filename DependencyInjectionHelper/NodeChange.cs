using Microsoft.CodeAnalysis;

namespace DependencyInjectionHelper
{
    public sealed class NodeChange
    {
        public NodeChange(SyntaxNode oldNode, SyntaxNode newNode)
        {
            OldNode = oldNode;
            NewNode = newNode;
        }

        public SyntaxNode OldNode { get; }

        public SyntaxNode NewNode { get; }
    }
}