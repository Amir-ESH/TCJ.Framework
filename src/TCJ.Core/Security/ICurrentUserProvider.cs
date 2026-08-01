// ****************************************************************************************************************************************************
// File: ICurrentUserProvider.cs
// Project: TCJ.Core
// Author: Amir Eslamzadeh
// Created: 1405-03-28 T12:03:39
// Modified: 1405-03-28 T12:03:39
// Version: 1.0.0
// ----------------------------------------------------------------------------------------------------------------------------------------------------
// Description: TODO: [File description should be inserted here.]
// ----------------------------------------------------------------------------------------------------------------------------------------------------
// Dependencies: TODO: [Add Dependencies like   - None (Pure abstraction)]
// ****************************************************************************************************************************************************

namespace TCJ.Core.Security;

/// <summary>
/// Provides information about the user associated with the current operation.
/// </summary>
/// <remarks>
/// This abstraction must not depend on ASP.NET Core.
/// The host application is responsible for providing its implementation.
/// </remarks>
public interface ICurrentUserProvider
{
    /// <summary>
    /// Gets the current user's identifier.
    /// Returns <c>null</c> for unauthenticated or system operations.
    /// </summary>
    long? UserId { get; }
}
