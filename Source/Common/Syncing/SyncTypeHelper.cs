using System;
using System.Collections.Generic;

namespace Multiplayer.Common;

public abstract class SyncTypeHelper
{
    public Type GetImplementationByIndex(Type baseType, ushort index)
    {
        return GetImplementations(baseType)[index];
    }

    public ushort GetIndexFromImplementation(Type baseType, Type type)
    {
        var index = GetImplementations(baseType).IndexOf(type);
        // -1 would wrap to 65535, the readers' null marker, while the caller still writes the object's payload
        if (index < 0)
            throw new Multiplayer.Client.SerializationException($"Type {type} is not a known implementation of {baseType}");
        return (ushort)index;
    }

    public abstract List<Type> GetImplementations(Type baseType);
}
