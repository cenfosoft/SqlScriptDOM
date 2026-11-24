//------------------------------------------------------------------------------
// <copyright file="VectorIndexType.cs" company="Microsoft">
//         Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
namespace Microsoft.SqlServer.TransactSql.ScriptDom
{
    /// <summary>
    /// The possible values for vector index type
    /// </summary>              
    public enum VectorIndexType
    {
        /// <summary>
        /// Disk-based approximate nearest neighbor search.
        /// </summary>
        DiskANN = 0
    }
}