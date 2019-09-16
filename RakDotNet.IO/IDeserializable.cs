using System;
using System.Collections.Generic;
using System.Text;

namespace RakDotNet.IO
{
    public interface IDeserializable
    {
        void Deserialize(BitReader reader);
    }
}
