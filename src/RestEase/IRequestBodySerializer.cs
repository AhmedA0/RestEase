﻿
namespace RestEase
{
    /// <summary>
    /// Helper which knows how to serialize a request body
    /// </summary>
    public interface IRequestBodySerializer
    {
        /// <summary>
        /// Serialize the given request body
        /// </summary>
        /// <param name="body">Body to serialize</param>
        /// <typeparam name="T">Type of the body to serialize</typeparam>
        /// <returns>String suitable for attaching as the requests's Content</returns>
        string SerializeBody<T>(T body);
    }
}
