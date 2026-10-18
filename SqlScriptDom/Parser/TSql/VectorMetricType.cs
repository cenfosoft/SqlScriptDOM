//------------------------------------------------------------------------------
// <copyright file="VectorMetricType.cs" company="Microsoft">
//         Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
namespace Microsoft.SqlServer.TransactSql.ScriptDom
{
    /// <summary>
    /// The possible values for vector index metric
    /// </summary>
    public enum VectorMetricType
    {
        /// <summary>
        /// Cosine distance.
        /// </summary>
        Cosine = 0,

        /// <summary>
        /// Dot-product distance.
        /// </summary>
        Dot = 1,

        /// <summary>
        /// Euclidean distance.
        /// </summary>
        Euclidean = 2
    }
}