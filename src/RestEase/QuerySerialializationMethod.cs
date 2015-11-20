﻿namespace RestEase
{
    /// <summary>
    /// Type of serialization that should be applied to the query parameter's value
    /// </summary>
    public enum QuerySerializationMethod
    {
        /// <summary>
        /// Serialized using its .ToString() method
        /// </summary>
        ToString,

        /// <summary>
        /// Serialized using the configured IRequestQueryParamSerializer (uses Json.NET by default)
        /// </summary>
        Serialized
    }
}