// SPDX-License-Identifier: MIT

namespace ZeyoCut.Core;

public class ZeyoCutException : Exception
{
    public ZeyoCutException(string message) : base(message)
    {
    }

    public ZeyoCutException(string message, Exception inner) : base(message, inner)
    {
    }
}
