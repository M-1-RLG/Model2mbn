using model2mbn.IO;

namespace model2mbn
{
    public struct VertexAttribute
    {
        public AttributeType Attribute;
        public DataType DataType;
        public float Scale;

        public void Write(FileOutput f)
        {
            f.writeInt((int)Attribute);
            f.writeInt((int)DataType);
            f.writeFloat(Scale);
        }

        public void Read()
        {
        }
        //public override string ToString()
        //{
        //    return $"";
        //}
    }
}
