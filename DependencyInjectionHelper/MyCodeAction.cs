using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;

namespace DependencyInjectionHelper
{
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