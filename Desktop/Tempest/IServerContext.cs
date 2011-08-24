﻿//
// IServerContext.cs
//
// Author:
//   Eric Maupin <me@ermau.com>
//
// Copyright (c) 2011 Eric Maupin
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;

namespace Tempest
{
	/// <summary>
	/// Contract for server contexts.
	/// </summary>
	public interface IServerContext
		: IContext
	{
		/// <summary>
		/// Raised when a connection is made.
		/// </summary>
		event EventHandler<ConnectionMadeEventArgs> ConnectionMade;

		/// <summary>
		/// Disconnects a connection after sending a disconnection message with <see cref="reason"/>.
		/// </summary>
		/// <param name="connection">This connection to disconnect.</param>
		/// <param name="reason">The reason given for disconnection.</param>
		/// <exception cref="ArgumentNullException"><paramref name="connection"/> or <paramref name="reason"/> is <c>null</c>.</exception>
		void DisconnectWithReason (IConnection connection, string reason);
	}
}