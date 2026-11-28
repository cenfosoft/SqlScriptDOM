//------------------------------------------------------------------------------
// <copyright file="Sql170ExpressionVisitor.cs" company="Microsoft">
//         Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Microsoft.SqlServer.TransactSql.ScriptDom
{
    internal sealed class Sql170ExpressionVisitor : TSqlConcreteFragmentVisitor
    {
        private const int VectorDimensionErrorNumber = 46146;
        private const int MinimumVectorDimension = 1;
        private const int MaximumVectorDimension = 1984;
        private const string VectorDimensionErrorMessage = "Vector dimension must be an integer between 1 and 1984.";

        private readonly IList<ParseError> _errors;

        internal Sql170ExpressionVisitor(IList<ParseError> errors)
        {
            _errors = errors ?? throw new ArgumentNullException(nameof(errors));
        }

        public override void ExplicitVisit(FunctionCall node)
        {
            if (!string.Equals(node.FunctionName?.Value, "VECTOR_DISTANCE", StringComparison.OrdinalIgnoreCase) ||
                node.Parameters.Count < 4 ||
                node.Parameters[3] is not IntegerLiteral { Value: var value } literal)
            {
                return;
            }

            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int dimension) ||
                dimension < MinimumVectorDimension ||
                dimension > MaximumVectorDimension)
            {
                AddParseError(literal, VectorDimensionErrorMessage);
            }
        }

        private void AddParseError(TSqlFragment fragment, string message)
        {
            _errors.Add(new ParseError(
                VectorDimensionErrorNumber,
                fragment.StartOffset,
                fragment.StartLine,
                fragment.StartColumn,
                message));
        }
    }
}
