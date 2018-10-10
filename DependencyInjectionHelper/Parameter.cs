using Microsoft.CodeAnalysis;

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
}