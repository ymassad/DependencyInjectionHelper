using Microsoft.CodeAnalysis;

namespace DependencyInjectionHelper
{
    public class Argument
    {
        public Argument(ITypeSymbol parameterType, string parameterName)
        {
            ParameterType = parameterType;
            ParameterName = parameterName;
        }

        public ITypeSymbol ParameterType { get; }

        public string ParameterName { get; }
    }
}