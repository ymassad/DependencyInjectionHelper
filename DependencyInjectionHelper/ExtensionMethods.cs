using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace DependencyInjectionHelper
{
    public static class ExtensionMethods
    {
        public static string GetFullName(this ITypeSymbol typeSymbol)
        {
            if (typeSymbol.TypeKind == TypeKind.TypeParameter)
                return typeSymbol.Name;

            if (typeSymbol is IArrayTypeSymbol array)
                return GetFullName(array.ElementType) + "[]";

            string name = typeSymbol.Name;

            if (typeSymbol is INamedTypeSymbol namedType)
            {
                if (namedType.IsGenericType && namedType.TypeArguments.Any())
                {
                    name += "<" + String.Join(", ", namedType.TypeArguments.Select(x => GetFullName(x))) + ">";
                }
            }

            if (typeSymbol.ContainingType != null)
                return GetFullName(typeSymbol.ContainingType) + "." + name;

            if (typeSymbol.ContainingNamespace != null)
            {
                if (typeSymbol.ContainingNamespace.IsGlobalNamespace)
                    return name;

                return GetFullname(typeSymbol.ContainingNamespace).Match(ns => ns + "." + name, () => name);
            }

            return name;
        }

        public static Maybe<string> GetFullname(this INamespaceSymbol @namespace)
        {
            if (@namespace.IsGlobalNamespace)
                return Maybe.NoValue;

            return GetFullname(@namespace.ContainingNamespace)
                .Match(x => x + "." + @namespace.Name, () => @namespace.Name);
        }
    }
}
