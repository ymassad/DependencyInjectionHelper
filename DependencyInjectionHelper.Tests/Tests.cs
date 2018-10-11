using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using NUnit.Framework;

namespace DependencyInjectionHelper.Tests
{
    [TestFixture]
    public class Tests
    {


        [Test]
        public void InvokingSimpleStaticMethod()
        {
            //Arrange
            var code =
@"
using System;

public static class Methods
{
    public static void DoSomething()
    {
        DoSomethingElse();
    }

    public static void DoSomethingElse()
    {
    }
}";

            var expectedChangedCode =
@"
using System;

public static class Methods
{
    public static void DoSomething(Action doSomethingElse)
    {
        doSomethingElse();
    }

    public static void DoSomethingElse()
    {
    }
}";

            var expectedContentAfterRefactoring =
                Utilities.NormalizeCode(
                    expectedChangedCode);

            //Act
            var actualContentAfterRefactoring =
                Utilities.NormalizeCode(
                    Utilities.ApplyRefactoring(
                        code,
                        x => SelectSpanForIdentifier(x, "DoSomethingElse")));

            //Assert
            Assert.AreEqual(expectedContentAfterRefactoring, actualContentAfterRefactoring);
        }

        [Test]
        public void InvokingStaticMethodThatTakesAnInt()
        {
            //Arrange
            var code =
                @"
using System;

public static class Methods
{
    public static void DoSomething()
    {
        DoSomethingElse(1);
    }

    public static void DoSomethingElse(int param1)
    {
    }
}";

            var expectedChangedCode =
                @"
using System;

public static class Methods
{
    public static void DoSomething(Action<int> doSomethingElse)
    {
        doSomethingElse(1);
    }

    public static void DoSomethingElse(int param1)
    {
    }
}";

            var expectedContentAfterRefactoring =
                Utilities.NormalizeCode(
                    expectedChangedCode);

            //Act
            var actualContentAfterRefactoring =
                Utilities.NormalizeCode(
                    Utilities.ApplyRefactoring(
                        code,
                        x => SelectSpanForIdentifier(x, "DoSomethingElse")));

            //Assert
            Assert.AreEqual(expectedContentAfterRefactoring, actualContentAfterRefactoring);
        }

        [Test]
        public void InvokingStaticMethodThatReturnsAnInt()
        {
            //Arrange
            var code =
                @"
using System;

public static class Methods
{
    public static void DoSomething()
    {
        int v = DoSomethingElse();
    }

    public static int DoSomethingElse() => 1;
}";

            var expectedChangedCode =
                @"
using System;

public static class Methods
{
    public static void DoSomething(Func<int> doSomethingElse)
    {
        int v = doSomethingElse();
    }

    public static int DoSomethingElse() => 1;
}";

            var expectedContentAfterRefactoring =
                Utilities.NormalizeCode(
                    expectedChangedCode);

            //Act
            var actualContentAfterRefactoring =
                Utilities.NormalizeCode(
                    Utilities.ApplyRefactoring(
                        code,
                        x => SelectSpanForIdentifier(x, "DoSomethingElse")));

            //Assert
            Assert.AreEqual(expectedContentAfterRefactoring, actualContentAfterRefactoring);
        }


        [Test]
        public void InvokingSimpleStaticMethod_AndThereIsACaller()
        {
            //Arrange
            var code =
                @"
using System;

public static class Methods
{
    public static void Caller()
    {
        DoSomething();
    }

    public static void DoSomething()
    {
        DoSomethingElse();
    }

    public static void DoSomethingElse()
    {
    }
}";

            var expectedChangedCode =
                @"
using System;

public static class Methods
{
    public static void Caller()
    {
        DoSomething(DoSomethingElse);
    }

    public static void DoSomething(Action doSomethingElse)
    {
        doSomethingElse();
    }

    public static void DoSomethingElse()
    {
    }
}";

            var expectedContentAfterRefactoring =
                Utilities.NormalizeCode(
                    expectedChangedCode);

            //Act
            var actualContentAfterRefactoring =
                Utilities.NormalizeCode(
                    Utilities.ApplyRefactoring(
                        code,
                        x => SelectSpanForIdentifier(x, "DoSomethingElse")));

            //Assert
            Assert.AreEqual(expectedContentAfterRefactoring, actualContentAfterRefactoring);
        }

        [Test]
        public void InvokingStaticMethodThatTakesInt_PassedIntIsAConstantValue_AndThereIsACaller_AndWeChooseToRemoveArgument()
        {
            //Arrange

            DependencyInjectionHelperCodeRefactoringProvider.WhatToDoWithArguments =
                arguments => arguments.Select(_ => WhatToDoWithArgument.Remove).ToImmutableArray();

            var code =
                @"
using System;

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
}";

            var expectedChangedCode =
                @"
using System;

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
}";

            var expectedContentAfterRefactoring =
                Utilities.NormalizeCode(
                    expectedChangedCode);

            //Act
            var actualContentAfterRefactoring =
                Utilities.NormalizeCode(
                    Utilities.ApplyRefactoring(
                        code,
                        x => SelectSpanForIdentifier(x, "DoSomethingElse")));

            //Assert
            Assert.AreEqual(expectedContentAfterRefactoring, actualContentAfterRefactoring);
        }


        [Test]
        public void InvokingStaticMethodThatTakesInt_PassedIntIsAParameter_AndThereIsACaller_AndWeChooseToRemoveArgument()
        {
            //Arrange

            DependencyInjectionHelperCodeRefactoringProvider.WhatToDoWithArguments =
                arguments => arguments.Select(_ => WhatToDoWithArgument.Remove).ToImmutableArray();

            var code =
                @"
using System;

public static class Methods
{
    public static void Caller()
    {
        DoSomething(1);
    }

    public static void DoSomething(int param2)
    {
        DoSomethingElse(param2);
    }

    public static void DoSomethingElse(int param1)
    {
    }
}";

            var expectedChangedCode =
                @"
using System;

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
}";

            var expectedContentAfterRefactoring =
                Utilities.NormalizeCode(
                    expectedChangedCode);

            //Act
            var actualContentAfterRefactoring =
                Utilities.NormalizeCode(
                    Utilities.ApplyRefactoring(
                        code,
                        x => SelectSpanForIdentifier(x, "DoSomethingElse")));

            //Assert
            Assert.AreEqual(expectedContentAfterRefactoring, actualContentAfterRefactoring);
        }

        [Test]
        public void InvokingStaticMethodThatTakesTwoInts_PassedIntsAreParameters_AndThereIsACaller_AndWeChooseToRemoveTheFirstArgument()
        {
            //Arrange

            DependencyInjectionHelperCodeRefactoringProvider.WhatToDoWithArguments =
                arguments =>
                    ImmutableArray<WhatToDoWithArgument>.Empty.AddRange(new []{
                        WhatToDoWithArgument.Remove,
                        WhatToDoWithArgument.Keep});

            var code =
                @"
using System;

public static class Methods
{
    public static void Caller()
    {
        DoSomething(1 , 2);
    }

    public static void DoSomething(int param3, int param4)
    {
        DoSomethingElse(param3, param4);
    }

    public static void DoSomethingElse(int param1, int param2)
    {
    }
}";

            var expectedChangedCode =
                @"
using System;

public static class Methods
{
    public static void Caller()
    {
        DoSomething(2, param2 => DoSomethingElse(1, param2));
    }

    public static void DoSomething(int param4, Action<int> doSomethingElse)
    {
        doSomethingElse(param4);
    }

    public static void DoSomethingElse(int param1, int param2)
    {
    }
}";

            var expectedContentAfterRefactoring =
                Utilities.NormalizeCode(
                    expectedChangedCode);

            //Act
            var actualContentAfterRefactoring =
                Utilities.NormalizeCode(
                    Utilities.ApplyRefactoring(
                        code,
                        x => SelectSpanForIdentifier(x, "DoSomethingElse")));

            //Assert
            Assert.AreEqual(expectedContentAfterRefactoring, actualContentAfterRefactoring);
        }

        [Test]
        public void InvokingStaticMethodThatTakesTwoIntsAndReturnsAnInt_PassedIntsAreParameters_AndThereIsACaller_AndWeChooseToRemoveTheFirstArgument()
        {
            //Arrange

            DependencyInjectionHelperCodeRefactoringProvider.WhatToDoWithArguments =
                arguments =>
                    ImmutableArray<WhatToDoWithArgument>.Empty.AddRange(new[]{
                        WhatToDoWithArgument.Remove,
                        WhatToDoWithArgument.Keep});

            var code =
                @"
using System;

public static class Methods
{
    public static int Caller()
    {
        return DoSomething(1 , 2);
    }

    public static int DoSomething(int param3, int param4)
    {
        return DoSomethingElse(param3, param4);
    }

    public static int DoSomethingElse(int param1, int param2)
    {
        return 1;
    }
}";

            var expectedChangedCode =
                @"
using System;

public static class Methods
{
    public static int Caller()
    {
        return DoSomething(2, param2 => DoSomethingElse(1, param2));
    }

    public static int DoSomething(int param4, Func<int, int> doSomethingElse)
    {
        return doSomethingElse(param4);
    }

    public static int DoSomethingElse(int param1, int param2)
    {
        return 1;
    }
}";

            var expectedContentAfterRefactoring =
                Utilities.NormalizeCode(
                    expectedChangedCode);

            //Act
            var actualContentAfterRefactoring =
                Utilities.NormalizeCode(
                    Utilities.ApplyRefactoring(
                        code,
                        x => SelectSpanForIdentifier(x, "DoSomethingElse")));

            //Assert
            Assert.AreEqual(expectedContentAfterRefactoring, actualContentAfterRefactoring);
        }

        private static TextSpan SelectSpanForIdentifier(SyntaxNode rootNode, string identifierName)
        {
            return rootNode.DescendantNodes()
                .OfType<IdentifierNameSyntax>()
                .Single(x => x.Identifier.Text == identifierName)
                .Span;
        }
    }
}
