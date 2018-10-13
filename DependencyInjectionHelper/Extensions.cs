using System;
using System.Collections.Generic;

namespace DependencyInjectionHelper
{
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

        public static Maybe<T> ToMaybe<T>(this T value) => value;
    }
}