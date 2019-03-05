# DependencyInjectionHelper

This is a Visual Studio extension that makes it easier to extract a method call into a function parameter.

For example, given this code:

```
public static class Methods
{
    public static void Caller()
    {
        DoSomething();
    }
    public static void DoSomething()
    {
        DoSomethingElse(1);
    }
    public static void DoSomethingElse(int param1)
    {
    }
}
```

You can use this extension to refactor the call to DoSomethingElse into a function parameter:

```
public static class Methods
{
    public static void Caller()
    {
        DoSomething(() => DoSomethingElse(1));
    }
    public static void DoSomething(Action doSomethingElse)
    {
        doSomethingElse();
    }
    public static void DoSomethingElse(int param1)
    {
    }
}
```

