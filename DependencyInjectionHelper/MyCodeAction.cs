using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;

namespace DependencyInjectionHelper
{
    public class MyCodeAction : CodeActionWithOptions
    {
        public override string Title { get; }

        private readonly Func<Maybe<ImmutableArray<WhatToDoWithArgument>>, CancellationToken, Task<Solution>> execute;
        private readonly Func<Maybe<ImmutableArray<WhatToDoWithArgument>>> getOptions;

        public MyCodeAction(
            string title,
            Func<Maybe<ImmutableArray<WhatToDoWithArgument>>, CancellationToken, Task<Solution>> execute,
            Func<Maybe<ImmutableArray<WhatToDoWithArgument>>> getOptions)
        {
            Title = title;
            this.execute = execute;
            this.getOptions = getOptions;
        }

        public override object GetOptions(CancellationToken cancellationToken)
        {
            return getOptions();
        }

        protected override async Task<IEnumerable<CodeActionOperation>> ComputeOperationsAsync(object options, CancellationToken cancellationToken)
        {
            var opt = (Maybe<ImmutableArray<WhatToDoWithArgument>>) options;

            var newSolution = await execute(opt, cancellationToken);

            return new CodeActionOperation[] {new ApplyChangesOperation(newSolution)};
        }
    }
}