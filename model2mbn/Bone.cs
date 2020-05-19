using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace model2mbn
{
    public class Bone
    {
        public int ID;
        public int ParentID = -1;
        public string ParentName;

        public string Name;

        public Vector3 Scale;

        public Vector3 Rotation;

        public Vector3 Translation;
    }
}
