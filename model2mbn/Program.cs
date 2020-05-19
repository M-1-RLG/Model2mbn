using System;
using model2mbn.IO;
using Assimp;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using System.IO;
using SPICA.Formats.CtrH3D;
using SPICA.Formats.CtrH3D.Model.Mesh;
using SPICA.PICA.Commands;
using System.Data;
using SPICA.Formats.CtrH3D.Model.Material;
using SPICA.Formats.CtrH3D.Texture;
using SPICA.Math3D;
using SPICA.Formats.CtrH3D.Model;
using SPICA.Formats.CtrH3D.Animation;
using SPICA.Formats.Common;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Drawing.Imaging;

namespace model2mbn
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Count() >= 1)
            {
                //TODO: Switch mbn types if all vertex attributes are the same for each mesh
                //TODO: Accept the argument "-s 33.3" as import scale

                Scene scene;
                H3D bch = new H3D();
                H3D Anim = new H3D();
                AssimpContext Importer = new AssimpContext();

                var Flags = PostProcessSteps.None;
                //Flags |= PostProcessSteps.JoinIdenticalVertices;//I want to keep n64 stages 1:1
                Flags |= PostProcessSteps.Triangulate;
                //Flags |= PostProcessSteps.RemoveRedundantMaterials;

                scene = Importer.ImportFile(args[0], Flags);

                var bones = GetBones(scene);//Bones
                var H3DBones = new List<H3DBone>();
                var BoneNames = new List<string>();//Bone Names
                var MeshBTables = new List<List<string>>();//Mesh bone indices
                var MeshNames = GetMeshNames(scene.RootNode, new List<string>());//Assimp is stupid
                bool HasBillBoard = false;

                //If the model contains a skeleton the other meshes have to be rigged too
                foreach (var name in MeshNames)
                {
                    if (name.ToLower().Contains("billboard"))
                    {
                        HasBillBoard = true;
                        if (!BoneNames.Contains("Billboard_Master"))
                            BoneNames.Add("Billboard_Master");
                    }
                }

                foreach (var b in bones)
                {
                    var Ntranslation = new Vector3()//Convert bones to n64 scale
                    {
                        X = b.Translation.X,
                        Y = b.Translation.Y,
                        Z = b.Translation.Z 
                    };

                    var h3db = new H3DBone
                    {
                        Translation = Ntranslation,
                        Scale = b.Scale,
                        Rotation = b.Rotation,
                        ParentIndex = (short)b.ParentID,
                        Name = b.Name
                    };

                    BoneNames.Add(b.Name);
                    H3DBones.Add(h3db);
                }

                var f = new FileOutput();//file

                //Header
                f.writeShort(6);//Version
                f.writeShort(-1);//Padding
                f.writeUInt(1);//Mesh flags
                f.writeUInt(0);//Vertex flags

                f.writeInt(scene.Meshes.Count);//Mesh count
                //End Header---------------------------------------------------------------


                //Vertex Attributes/Mesh Data
                foreach (var mesh in scene.Meshes)
                {
                    //Get this meshes vertex attributes
                    List<VertexAttribute> Attributes = GetAttributes(mesh);
                    List<string> BoneTable = new List<string>();

                    var MeshName = MeshNames[scene.Meshes.IndexOf(mesh)];
                    var bmesh = CreateGenericMesh(bch, MeshName);

                    bmesh.MaterialIndex = (ushort)mesh.MaterialIndex;
                    bmesh.SubMeshes[0].BoneIndicesCount = (ushort)mesh.Bones.Count;


                    var BIndex = 0;//Bone Index
                    f.writeInt(1);//Submesh count
                    if (mesh.HasBones)
                    {
                        f.writeInt(mesh.Bones.Count);//BoneTable count
                        float MaxWeight = 0;

                        foreach (var bone in mesh.Bones)
                        {
                            //Write bonetable for bch/mbn
                            if (BoneNames.Contains(bone.Name))
                            {
                                f.writeInt(BoneNames.IndexOf(bone.Name));
                                bmesh.SubMeshes[0].BoneIndices[BIndex] = (ushort)BoneNames.IndexOf(bone.Name);
                            }
                            else
                                f.writeInt(0);

                            //Do this to check if the mesh is smooth skinning
                            foreach (var weight in bone.VertexWeights)
                                if (weight.Weight > MaxWeight)
                                    MaxWeight = weight.Weight;

                            BIndex++;
                            BoneTable.Add(bone.Name);
                        }

                        if (mesh.Bones.Count >= 1 && MaxWeight == 1)
                        {
                            bmesh.Skinning = H3DMeshSkinning.Mixed;
                            bmesh.SubMeshes[0].Skinning = H3DSubMeshSkinning.Rigid;
                        }
                        else
                        {
                            bmesh.Skinning = H3DMeshSkinning.Mixed;
                            bmesh.SubMeshes[0].Skinning = H3DSubMeshSkinning.Smooth;
                        }

                        MeshBTables.Add(BoneTable);
                    }
                    else if(MeshName.ToLower().Contains("billboard"))
                    {
                        var MeshCenter = GetMeshCenter(mesh);
                        int index = 0;
                        string BName = $"BillboardBone_{index}";


                        //Make sure of no duplicate names
                        while(bch.Models[0].Skeleton.Contains(BName)){
                            BName = $"BillboardBone_{index}";
                            index++;
                        }

                        //Make dad bone, then his children
                        if (!bch.Models[0].Skeleton.Contains("Billboard_Master"))
                        {
                            bch.Models[0].Skeleton.Add(new H3DBone()
                            {
                                Scale = new Vector3(1, 1, 1),
                                Name = "Billboard_Master",
                                ParentIndex = -1
                            });
                            bch.Models[0].Skeleton.Last().CalculateTransform(bch.Models[0].Skeleton);
                        }

                        bch.Models[0].Skeleton.Add(new H3DBone()
                        {
                            Name = BName,
                            Translation = new Vector3(MeshCenter.X , MeshCenter.Y , MeshCenter.Z ),
                            ParentIndex = (short)BoneNames.IndexOf("Billboard_Master"),
                            Scale = new Vector3(1,1,1)
                        });

                        //Calculating transform removes billboard mode
                        bch.Models[0].Skeleton.Last().CalculateTransform(bch.Models[0].Skeleton);
                        bch.Models[0].Skeleton.Last().BillboardMode = H3DBillboardMode.YAxial;

                        mesh.Bones.Add(new Assimp.Bone() { Name = BName });

                        for (int j = 0; j < mesh.Vertices.Count; j++){
                            var v = mesh.Vertices[j];
                            //Transform mesh to 0,0,0 so billboard can rotate properly
                            var tp = OpenTK.Vector3.TransformPosition(new OpenTK.Vector3(v.X, v.Y, v.Z), OpenTK.Matrix4.CreateTranslation(MeshCenter.X, MeshCenter.Y, MeshCenter.Z).Inverted());
                            mesh.Vertices[j] = new Vector3D(tp.X, tp.Y, tp.Z);

                            //Add bone weights
                            mesh.Bones[0].VertexWeights.Add(new VertexWeight(j, 1));
                        }

                        //Update bch bone table
                        //Why does mbn contain the bone table if bch does too????
                        bmesh.SubMeshes[0].BoneIndicesCount = 1;
                        bmesh.SubMeshes[0].BoneIndices[0] = (ushort)(bch.Models[0].Skeleton.Count-1);

                        f.writeInt(1);//Bone table count
                        f.writeInt(bch.Models[0].Skeleton.Count-1);//Bone index

                        bmesh.Skinning = H3DMeshSkinning.Mixed;
                        bmesh.SubMeshes[0].Skinning = H3DSubMeshSkinning.Rigid;
                        bch.Models[0].Flags = H3DModelFlags.HasSkeleton;
                        Attributes.Add(new VertexAttribute(){Attribute = AttributeType.BoneIndices, DataType = DataType.Byte, Scale = 1f});
                        Attributes.Add(new VertexAttribute(){Attribute = AttributeType.BoneWeights, DataType = DataType.Byte, Scale = 0.01f});

                        BoneTable.Add(BName);
                        BoneNames.Add(BName);
                        MeshBTables.Add(BoneTable);
                    }
                    else{
                        if(HasBillBoard)
                        {
                            //If the model has a skeleton, all meshes must be rigged.
                            //So you create a dummy bone for unrigged meshes and rig them to the dummy bone

                            f.writeInt(1);
                            f.writeInt(BoneNames.IndexOf("Billboard_Master"));
                            bmesh.SubMeshes[0].Skinning = H3DSubMeshSkinning.Rigid;

                            bmesh.SubMeshes[0].BoneIndicesCount = 1;
                            bmesh.SubMeshes[0].BoneIndices[0] = (ushort)BoneNames.IndexOf("Billboard_Master");

                            mesh.Bones.Add(new Assimp.Bone() { Name = "Billboard_Master" });
                            for (int j = 0; j < mesh.Vertices.Count; j++){
                                mesh.Bones[0].VertexWeights.Add(new VertexWeight(j, 1));
                            }

                            Attributes.Add(new VertexAttribute() { Attribute = AttributeType.BoneIndices, DataType = DataType.Byte, Scale = 1f });
                            Attributes.Add(new VertexAttribute() { Attribute = AttributeType.BoneWeights, DataType = DataType.Byte, Scale = 0.01f });

                            BoneTable.Add("Billboard_Master");
                            MeshBTables.Add(BoneTable);
                        }
                        else
                        {
                            f.writeInt(mesh.Bones.Count);
                            BoneTable.Add("Null");
                            MeshBTables.Add(BoneTable);
                            bmesh.SubMeshes[0].Skinning = H3DSubMeshSkinning.None;
                        }
                    }

                    //Face(s) count
                    f.writeInt(mesh.FaceCount * 3);
                    f.writeInt(Attributes.Count);

                    //Write Vertex Attributes
                    foreach (var va in Attributes)
                        va.Write(f);

                    f.writeInt(mesh.Vertices.Count * GetStrideSize(Attributes, true));//Vertex buffer size
                    bch.Models[0].AddMesh(bmesh);
                }
                f.align(32, -1);

                foreach (var material in scene.Materials)
                {
                    if (!bch.Materials.Contains($"{material.Name}@Model"))
                    {
                        var mat = new H3DMaterial();
                        string TexName = Path.GetFileNameWithoutExtension(material.TextureDiffuse.FilePath);

                        if (!bch.Textures.Contains(TexName) && File.Exists($@"{Path.GetDirectoryName(args[0])}\{Path.GetFileName(material.TextureDiffuse.FilePath)}"))
                            bch.Textures.Add(new H3DTexture(TexName, GetBitmap32($@"{Path.GetDirectoryName(args[0])}\{Path.GetFileName(material.TextureDiffuse.FilePath)}")));

                        mat.EnabledTextures[0] = true;
                        mat.Texture0Name = TexName;
                        mat.Name = material.Name;
                        mat.MaterialParams.Name = $"{material.Name}@{bch.Models[0].Name}";
                        mat.MaterialParams.ModelReference = $"{material.Name}@{bch.Models[0].Name}";

                        GetSimpleMaterial(mat.MaterialParams);
                        CheckMaterial(mat, material.Name);

                        bch.Models[0].Materials.Add(mat);
                        bch.Materials.Add(mat.MaterialParams);
                    }
                }

                if (H3DBones.Count >= 1)
                {
                    bch.Models[0].Flags = H3DModelFlags.HasSkeleton;
                    foreach (var b in H3DBones)
                    {
                        b.CalculateTransform(bch.Models[0].Skeleton);
                        bch.Models[0].Skeleton.Add(b);
                    }
                }

                var i = 0;
                foreach (var mesh in scene.Meshes)
                {
                    List<VertexAttribute> Attributes = GetAttributes(mesh);

                    //Yes i'm too lazy to fix this and do it properly
                    if (mesh.HasBones)
                        WriteVertexBuffer(f, mesh, Attributes, MeshBTables[i]);
                    else
                        WriteVertexBuffer(f, mesh, Attributes, new List<string>());
                    f.align(32, -1);

                    foreach (var faces in mesh.Faces)
                    {
                        f.writeUShort((ushort)faces.Indices[0]);
                        f.writeUShort((ushort)faces.Indices[1]);
                        f.writeUShort((ushort)faces.Indices[2]);
                    }
                    f.align(32, -1);

                    //Redundant materials are ignored
                    var MaterialName = scene.Materials[mesh.MaterialIndex].Name;

                    if (bch.Models[0].Materials.Contains(MaterialName))
                    {
                        bch.Models[0].Meshes[i].UpdateBoolUniforms(bch.Models[0].Materials[MaterialName]);
                        bch.Models[0].Meshes[i].MaterialIndex = (ushort)bch.Models[0].Materials.Find(MaterialName);
                    }
                    else
                        bch.Models[0].Meshes[i].MaterialIndex = 0;
                    i++;
                }

                if (scene.HasAnimations)
                {
                    foreach (var SA in scene.Animations)
                    {
                        //SA = scene animation
                        Anim.SkeletalAnimations.Add(CreateAnimation(SA));
                    }
                }

                f.save($@"{Path.GetDirectoryName(args[0])}\\normal.mbn");
                H3D.Save($@"{Path.GetDirectoryName(args[0])}\\normal.bch", bch);

                //Temporary
                //Stages with load forever if you have more than one animation
                if(scene.HasAnimations)
                {
                    while (Anim.SkeletalAnimations.Count != 1)
                        Anim.SkeletalAnimations.Remove(Anim.SkeletalAnimations.Count - 1);

                    H3D.Save($@"{Path.GetDirectoryName(args[0])}\\anim.bch", Anim);
                }
            }
        }


        #region MBN
        private static int GetStrideSize(List<VertexAttribute> attributes, bool ReturnAlignment)
        {
            //TODO: Finish this and do it properly
            var i = 0;
            foreach (var a in attributes)
            {
                switch (a.DataType)
                {
                    case DataType.SByte:
                        if (a.Attribute == AttributeType.Position || a.Attribute == AttributeType.Normal)
                            i += sizeof(sbyte) * 3;
                        else if (a.Attribute == AttributeType.Color)
                            i += sizeof(sbyte) * 4;
                        else
                            i += sizeof(sbyte) * 2;
                        break;
                    case DataType.Byte:
                        if (a.Attribute == AttributeType.Position || a.Attribute == AttributeType.Normal)
                            i += sizeof(byte) * 3;
                        else if (a.Attribute == AttributeType.Color)
                            i += sizeof(byte) * 4;
                        else
                            i += sizeof(byte) * 2;
                        break;
                    case DataType.Float:
                        if (a.Attribute == AttributeType.Position || a.Attribute == AttributeType.Normal)
                            i += sizeof(float) * 3;
                        else if (a.Attribute == AttributeType.Color)
                            i += sizeof(float) * 4;
                        else
                            i += sizeof(float) * 2;
                        break;
                    case DataType.SShort:
                        if (a.Attribute == AttributeType.Position || a.Attribute == AttributeType.Normal)
                            i += sizeof(short) * 3;
                        else if (a.Attribute == AttributeType.Color)
                            i += sizeof(short) * 4;
                        else
                            i += sizeof(short) * 2;
                        break;
                }
            }

            if (i % 2 == 1 && ReturnAlignment)
                i++;

            return i;
        }

        public static void WriteDataType(FileOutput f, VertexAttribute Type, float value)
        {
            switch (Type.DataType)
            {
                case DataType.SByte:
                    f.writeSByte(Convert.ToSByte(value));
                    break;
                case DataType.Byte:
                    f.writeByte(Convert.ToByte(value));
                    break;
                case DataType.Float:
                    f.writeFloat(value);
                    break;
                case DataType.SShort:
                    f.writeShort(Convert.ToInt16(value));
                    break;
            }
        }

        private static List<VertexAttribute> GetAttributes(Mesh mesh)
        {
            var va = new List<VertexAttribute>();

            //Position
            va.Add(new VertexAttribute()
            {
                Attribute = AttributeType.Position,
                DataType = DataType.Float,
                Scale = 1f
            });

            if (mesh.HasNormals)
            {
                va.Add(new VertexAttribute()
                {
                    Attribute = AttributeType.Normal,
                    DataType = DataType.SByte,
                    Scale = 0.007874f
                });
            }
            if (mesh.HasVertexColors(0))
            {
                va.Add(new VertexAttribute()
                {
                    Attribute = AttributeType.Color,
                    DataType = DataType.Byte,
                    Scale = 0.003922f
                });
            }
            if (mesh.HasTextureCoords(0))
            {
                va.Add(new VertexAttribute()
                {
                    Attribute = AttributeType.UV0,
                    DataType = DataType.SShort,
                    Scale = 0.0003092868f
                });
            }
            if (mesh.HasTextureCoords(1))
            {
                va.Add(new VertexAttribute()
                {
                    Attribute = AttributeType.UV1,
                    DataType = DataType.SShort,
                    Scale = 0.0003092868f
                });
            }
            if (mesh.HasBones)
            {
                va.Add(new VertexAttribute()
                {
                    Attribute = AttributeType.BoneIndices,
                    DataType = DataType.Byte,
                    Scale = 1f
                });

                va.Add(new VertexAttribute()
                {
                    Attribute = AttributeType.BoneWeights,
                    DataType = DataType.Byte,
                    Scale = 0.01f
                });
            }
            if (mesh.HasTextureCoords(2))
            {
                va.Add(new VertexAttribute()
                {
                    Attribute = AttributeType.UV0,
                    DataType = DataType.SShort,
                    Scale = 0.0003092868f
                });
            }

            return va;
        }

        private static void WriteVertexBuffer(FileOutput f, Mesh mesh, List<VertexAttribute> attributes, List<string> BTable)
        {
            List<Vector3D> Positions = mesh.Vertices;
            List<Vector3D> Normals = mesh.Normals;
            List<Color4D> Colors = mesh.VertexColorChannels[0];
            List<Vector3D> UV0 = mesh.TextureCoordinateChannels[0];
            List<Vector3D> UV1 = mesh.TextureCoordinateChannels[1];
            List<Vector3D> UV2 = mesh.TextureCoordinateChannels[2];
            List<Vector4> Indices = new List<Vector4>();
            List<Vector4> Weights = new List<Vector4>();
            var StrideSize = GetStrideSize(attributes, false);

            //Get weights/indices
            if (mesh.HasBones)
            {
                var BIndices = new List<List<int>>();
                var BWeights = new List<List<float>>();

                for (int i = 0; i < Positions.Count; i++)
                {
                    BIndices.Add(new List<int>());
                    BWeights.Add(new List<float>());
                }

                foreach (var b in mesh.Bones)
                {
                    foreach (var w in b.VertexWeights)
                    {
                        BIndices[w.VertexID].Add(BTable.IndexOf(b.Name));
                        BWeights[w.VertexID].Add(w.Weight);
                    }
                }

                for (int i = 0; i < Positions.Count; i++)
                {
                    if (BIndices[i].Count > 0)
                    {
                        var bi = new Vector4();
                        var bw = new Vector4();
                        //X
                        if (BIndices[i].Count >= 1) {
                            bi.X = BIndices[i][0];
                            bw.X = BWeights[i][0];
                        }
                        //Y
                        if (BIndices[i].Count >= 2) {
                            bi.Y = BIndices[i][1];
                            bw.Y = BWeights[i][1];
                        }
                        //Z
                        if (BIndices[i].Count >= 3) {
                            bi.Z = BIndices[i][2];
                            bw.Z = BWeights[i][2];
                        }
                        //W
                        if (BIndices[i].Count >= 4) {
                            bi.W = BIndices[i][3];
                            bw.W = BWeights[i][3];
                        }

                        Indices.Add(bi);
                        Weights.Add(bw);
                    }
                }
            }

            for (int i = 0; i < Positions.Count; i++)
            {
                foreach (var va in attributes)
                {
                    if (va.Attribute == AttributeType.Position) {
                        WriteDataType(f, va, Positions[i].X );//Divide by 33.3 to convert to smash 4 scale
                        WriteDataType(f, va, Positions[i].Y );
                        WriteDataType(f, va, Positions[i].Z );
                    }

                    if (va.Attribute == AttributeType.Normal) {
                        WriteDataType(f, va, Normals[i].X * 127);
                        WriteDataType(f, va, Normals[i].Y * 127);
                        WriteDataType(f, va, Normals[i].Z * 127);

                        if (StrideSize % 2 == 1 && Colors.Count == 0)
                            f.writeByte(0);
                    }

                    if (va.Attribute == AttributeType.Color) {

                        WriteDataType(f, va, Colors[i].R * 255);
                        WriteDataType(f, va, Colors[i].G * 255);
                        WriteDataType(f, va, Colors[i].B * 255);
                        WriteDataType(f, va, Colors[i].A * 255);

                        if (StrideSize % 2 == 1 && Colors.Count >= 1)
                            f.writeByte(0);
                    }

                    if (va.Attribute == AttributeType.UV0) {
                        WriteDataType(f, va, UV0[i].X / 0.0003092868f);
                        WriteDataType(f, va, UV0[i].Y / 0.0003092868f);
                    }

                    if (va.Attribute == AttributeType.UV1) {
                        WriteDataType(f, va, UV1[i].X / 0.0003092868f);
                        WriteDataType(f, va, UV1[i].Y / 0.0003092868f);
                    }

                    if (va.Attribute == AttributeType.UV2) {
                        WriteDataType(f, va, UV2[i].X / 0.0003092868f);
                        WriteDataType(f, va, UV2[i].Y / 0.0003092868f);
                    }

                    if (va.Attribute == AttributeType.BoneIndices) {

                        //Indices
                        WriteDataType(f, va, Indices[i].X);
                        WriteDataType(f, va, Indices[i].Y);
                        //WriteDataType(f, va, Indices[i].Z);
                        //WriteDataType(f, va, Indices[i].W);

                        //Weights
                        WriteDataType(f, va, Weights[i].X * 100);
                        WriteDataType(f, va, Weights[i].Y * 100);
                        //WriteDataType(f, va, Weights[i].Z);
                        //WriteDataType(f, va, Weights[i].W);
                    }
                }
            }
        }
        #endregion

        //Getting assimp bones is hell
        #region Getting Skeleton
        private static List<Bone> GetBones(Scene scene)
        {
            var Node = scene.RootNode.FindNode("TopJoint");//S60_MarioPast00_Castle_bone_id
            var bones = new List<Bone>();
            var Bnames = new List<string>();

            if (Node != null)
            {
                Assimp.Matrix4x4 input = Node.Transform;
                OpenTK.Matrix4 Transform = new OpenTK.Matrix4(input.A1, input.B1, input.C1, input.D1,
                                              input.A2, input.B2, input.C2, input.D2,
                                              input.A3, input.B3, input.C3, input.D3,
                                              input.A4, input.B4, input.C4, input.D4);

                OpenTK.Vector3 Rotation = ToEulerAngles(Transform.ExtractRotation());
                OpenTK.Vector3 Translation = Transform.ExtractTranslation();

                Bnames.Add("TopJoint");

                bones.Add(new Bone()
                {
                    ID = 0,
                    ParentID = -1,
                    Scale = Vector3.One,
                    Translation = new Vector3(Translation.X, Translation.Y, Translation.Z),
                    Rotation = new Vector3(Rotation.X, Rotation.Y, Rotation.Z),
                    Name = Node.Name
                });

                if (Node.HasChildren)
                {
                    foreach (var b in Node.Children) {
                        GetBone(b, bones, Bnames, scene);
                    }
                }

                var Names = new List<string>();

                foreach (var b in bones) {
                    Names.Add(b.Name);
                }

                foreach (var b in bones) {
                    b.ParentID = Names.IndexOf(b.ParentName);
                }
            }
            return bones;
        }

        private static void GetBone(Node node, List<Bone> bones, List<string> bnames, Scene scene)
        {
            Node Parent = scene.RootNode.FindNode(node.Parent.Name);
            Assimp.Matrix4x4 input = node.Transform;
            OpenTK.Matrix4 Transform = new OpenTK.Matrix4(input.A1, input.B1, input.C1, input.D1,
                                          input.A2, input.B2, input.C2, input.D2,
                                          input.A3, input.B3, input.C3, input.D3,
                                          input.A4, input.B4, input.C4, input.D4);

            OpenTK.Vector3 Rotation = ToEulerAngles(Transform.ExtractRotation());
            OpenTK.Vector3 Translation = Transform.ExtractTranslation();

            bnames.Add(node.Name);
            bones.Add(new Bone()
            {
                ID = bnames.IndexOf(node.Name),
                ParentName = Parent.Name,
                Translation = new Vector3(Translation.X, Translation.Y, Translation.Z),
                Rotation = new Vector3(Rotation.X, Rotation.Y, Rotation.Z),
                Scale = Vector3.One,
                Name = node.Name
            });

            if (node.HasChildren)
            {
                foreach (var b in node.Children)
                {
                    GetBone(b, bones, bnames, scene);
                }
            }
        }

        private static OpenTK.Vector3 ToEulerAngles(OpenTK.Quaternion q)
        {
            OpenTK.Matrix4 mat = OpenTK.Matrix4.CreateFromQuaternion(q);
            float x, y, z;
            y = (float)Math.Asin(Clamp(mat.M13, -1, 1));

            if (Math.Abs(mat.M13) < 0.99999)
            {
                x = (float)Math.Atan2(-mat.M23, mat.M33);
                z = (float)Math.Atan2(-mat.M12, mat.M11);
            }
            else
            {
                x = (float)Math.Atan2(mat.M32, mat.M22);
                z = 0;
            }
            return new OpenTK.Vector3(x, y, z) * -1;
        }

        private static float Clamp(float v, float min, float max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }
        #endregion


        #region BCH
        private static H3DMesh CreateGenericMesh(H3D scene, string MeshName)
        {
            if (scene.Models.Count < 1)
            {
                var mdl = new H3DModel{
                    Name = "Model",
                    MeshNodesTree = new H3DPatriciaTree()
                };

                mdl.MeshNodesTree.Clear();
                //if World Transform isn't set the mesh will be invisible
                mdl.WorldTransform = new Matrix3x4(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0);
                scene.Models.Add(mdl);
            }
            if(!scene.Models[0].MeshNodesTree.Contains(MeshName))
            {
                scene.Models[0].MeshNodesTree.Add(MeshName);
                scene.Models[0].MeshNodesVisibility.Add(true);
            }

            var list = new List<int> { scene.Models[0].Meshes.Count };
            H3DMetaData meta = new H3DMetaData
            {
                //ShapeId is used for mbn mesh sorting
                new H3DMetaDataValue(){
                    Name = "ShapeId",
                    Type = H3DMetaDataType.Integer,
                    Values = list
                }
            };


            H3DMesh mesh = new H3DMesh()
            {
                NodeIndex = (ushort)scene.Models[0].MeshNodesTree.Find(MeshName),
                SubMeshes = new List<H3DSubMesh>(),
                MetaData = meta,
                RawBuffer = new byte[0],
                Parent = scene.Models[0]
            };

            /*//For fighters only i think
            mesh.FixedAttributes.Add(new PICAFixedAttribute()
            { 
                Name = PICAAttributeName.BoneIndex, 
                Value = new PICAVectorFloat24(0, 0, 0, 1) 
            });
            */

            mesh.SubMeshes.Add(new H3DSubMesh()
            {
                PrimitiveMode = PICAPrimitiveMode.Triangles,
                BoneIndices = new ushort[0],
                Indices = new ushort[0]
            });

            return mesh;
        }

        /// <summary>
        /// Make a default n64 like material
        /// </summary>
        /// <param name="mat"></param>
        private static void GetSimpleMaterial(H3DMaterialParams mat)
        {
            //Most common is: 7@CustumShader
            mat.ShaderReference = "7@CustumShader";

            //Alpha doesn't exist for spec0-1/Ambient/Emission Colors
            mat.AmbientColor = new RGBA(76, 76, 76, 0);
            mat.EmissionColor = new RGBA(0, 0, 0, 0);
            mat.DiffuseColor = new RGBA(255, 255, 255, 255);
            mat.BlendColor = new RGBA(0, 0, 0, 255);
            mat.Constant0Color = new RGBA(0, 0, 0, 255);
            mat.Specular0Color = new RGBA(255, 255, 255, 0);
            mat.TexEnvBufferColor = new RGBA(0, 0, 0, 255);

            mat.ColorScale = 1f;
            mat.ColorOperation.BlendMode = PICABlendMode.Blend;

            mat.BlendFunction.AlphaSrcFunc = PICABlendFunc.One;
            mat.BlendFunction.ColorSrcFunc = PICABlendFunc.One;


            mat.DepthBufferRead = true;
            mat.DepthBufferWrite = true;
            mat.ColorBufferWrite = true;
            mat.DepthColorMask.AlphaWrite = true;
            mat.DepthColorMask.BlueWrite = true;
            mat.DepthColorMask.DepthFunc = PICATestFunc.Less;
            mat.DepthColorMask.DepthWrite = true;
            mat.DepthColorMask.Enabled = true;
            mat.DepthColorMask.GreenWrite = true;
            mat.DepthColorMask.RedWrite = true;

            mat.TexEnvStages[0].Color = new RGBA(0, 0, 0, 255);
            mat.TexEnvStages[1].Color = new RGBA(0, 0, 0, 255);
            mat.TexEnvStages[2].Color = new RGBA(0, 0, 0, 255);
            mat.TexEnvStages[3].Color = new RGBA(0, 0, 0, 255);
            mat.TexEnvStages[4].Color = new RGBA(0, 0, 0, 255);
            mat.TexEnvStages[5].Color = new RGBA(0, 0, 0, 255);

            //Basic combiner stage
            //Color: Vertex color *= Texture0
            //Alpha: Texture0 Alpha *= Vertex Alpha
            mat.TexEnvStages[0].Combiner.Alpha = PICATextureCombinerMode.Modulate;
            mat.TexEnvStages[0].Combiner.Color = PICATextureCombinerMode.Modulate;
            mat.TexEnvStages[0].Operand.Alpha[0] = PICATextureCombinerAlphaOp.Alpha;
            mat.TexEnvStages[0].Operand.Alpha[1] = PICATextureCombinerAlphaOp.Alpha;
            mat.TexEnvStages[0].Operand.Alpha[2] = PICATextureCombinerAlphaOp.Alpha;

            mat.TexEnvStages[0].Operand.Color[0] = PICATextureCombinerColorOp.Color;
            mat.TexEnvStages[0].Operand.Color[1] = PICATextureCombinerColorOp.Color;
            mat.TexEnvStages[0].Operand.Color[2] = PICATextureCombinerColorOp.Color;

            mat.TexEnvStages[0].Scale.Alpha = PICATextureCombinerScale.One;
            mat.TexEnvStages[0].Scale.Color = PICATextureCombinerScale.One;

            mat.TexEnvStages[0].Source.Alpha[0] = PICATextureCombinerSource.PrimaryColor;
            mat.TexEnvStages[0].Source.Alpha[1] = PICATextureCombinerSource.Texture0;

            mat.TexEnvStages[0].Source.Color[0] = PICATextureCombinerSource.PrimaryColor;
            mat.TexEnvStages[0].Source.Color[1] = PICATextureCombinerSource.Texture0;

            //----------------------------------------------------------------------------------
            //Set the rest of the combiner stages to pass the previous stage (Yes this is necessary)
            for (int i = 1; i < 6; i++)
            {
                mat.TexEnvStages[i].Source.Color[0] = PICATextureCombinerSource.Previous;
                mat.TexEnvStages[i].Source.Alpha[0] = PICATextureCombinerSource.Previous;
                mat.TexEnvStages[i].Combiner.Alpha = PICATextureCombinerMode.Replace;
                mat.TexEnvStages[i].Combiner.Color = PICATextureCombinerMode.Replace;
            }

            mat.TextureCoords[0].TransformType = H3DTextureTransformType.DccMaya;
            mat.TextureCoords[0].MappingType = H3DTextureMappingType.UvCoordinateMap;
            mat.TextureCoords[0].Scale = new Vector2(1, 1);
            mat.TextureCoords[0].Flags = H3DTextureCoordFlags.IsDirty;

            //SPICA is stupid when updating bool uniforms
            //It thinks if TextureCoords mapping type is "UvCoordinateMap", that the model uses UV1/2
            //This is because it can't check mbn attributes unless the mbn is open
            mat.TextureCoords[1].MappingType = H3DTextureMappingType.CameraSphereEnvMap;
            mat.TextureCoords[2].MappingType = H3DTextureMappingType.CameraSphereEnvMap;
        }

        /// <summary>
        /// Check texture wraping and cull modes
        /// </summary>
        /// <param name="mat"></param>
        /// <param name="name"></param>
        private static void CheckMaterial(H3DMaterial mat, string name)
        {
            //Set default WrapMode
            mat.TextureMappers[0].WrapU = PICATextureWrap.Repeat;
            mat.TextureMappers[0].WrapV = PICATextureWrap.Repeat;

            //Check mirroring
            if (name.Contains("ClampS"))
                mat.TextureMappers[0].WrapU = PICATextureWrap.ClampToEdge;
            if (name.Contains("ClampT"))
                mat.TextureMappers[0].WrapV = PICATextureWrap.ClampToEdge;
            if (name.Contains("MirrorS"))
                mat.TextureMappers[0].WrapU = PICATextureWrap.Mirror;
            if (name.Contains("MirrorT"))
                mat.TextureMappers[0].WrapV = PICATextureWrap.Mirror;

            if (name.Contains("CullBoth"))
                mat.MaterialParams.FaceCulling = PICAFaceCulling.Never;
            if (name.Contains("CullBack"))
                mat.MaterialParams.FaceCulling = PICAFaceCulling.BackFace;
            if (name.Contains("CullFront"))
                mat.MaterialParams.FaceCulling = PICAFaceCulling.FrontFace;

            mat.MaterialParams.AlphaTest.Enabled = true;
            mat.MaterialParams.AlphaTest.Function = PICATestFunc.Greater;

            //Set AlphaTest.Reference to 0 instead of 127, Is basically how it looks on n64
            //That's why there's yellow outlines on zebes background items
            if (name.Contains("Transparent"))
            {
                mat.MaterialParams.AlphaTest.Reference = 127;
            }
            else
                mat.MaterialParams.AlphaTest.Reference = 0;

            mat.TextureMappers[0].BorderColor = new RGBA(0, 0, 0, 255);
            mat.TextureMappers[0].MagFilter = H3DTextureMagFilter.Linear;
            mat.TextureMappers[0].MinFilter = H3DTextureMinFilter.Linear;

        }

        //Animation
        private static H3DAnimation CreateAnimation(Animation s)
        {
            var a = new H3DAnimation();
            var UsedNames = new List<string>();

            if(s.HasNodeAnimations)
            {
                a.FramesCount = (float)Math.Truncate(s.DurationInTicks);
                a.AnimationFlags = H3DAnimationFlags.IsLooping;
                a.AnimationType = H3DAnimationType.Skeletal;
                a.CurvesCount = 1;
                a.Name = s.Name;
                foreach (var e in s.NodeAnimationChannels)
                {
                    if (!UsedNames.Contains(s.Name))
                    {
                        //Create Element
                        H3DAnimationElement element = new H3DAnimationElement
                        {
                            Name = e.NodeName,
                            PrimitiveType = H3DPrimitiveType.Transform,
                            TargetType = H3DTargetType.Bone
                        };

                        var con = new H3DAnimTransform();
                        SetAnimTransforms(con, (float)s.DurationInTicks);

                        //Translation XYZ
                        //Remove useless keyframes
                        if (e.PositionKeys.TrueForAll(i => i.Value.Equals(e.PositionKeys.First().Value)))
                        {
                            //If you don't add these dummy frames the model won't animate
                            con.TranslationX.KeyFrames.Add(new KeyFrame());
                            con.TranslationY.KeyFrames.Add(new KeyFrame());
                            con.TranslationZ.KeyFrames.Add(new KeyFrame());
                        }
                        else
                        {
                            //TODO: optimize keyframes
                            con.TranslationX.CurveIndex = a.CurvesCount;
                            con.TranslationY.CurveIndex = (ushort)(a.CurvesCount + 1);
                            con.TranslationZ.CurveIndex = (ushort)(a.CurvesCount + 2);
                            a.CurvesCount += 3;

                            foreach (var tran in e.PositionKeys)
                            {
                                con.TranslationX.KeyFrames.Add(new KeyFrame()
                                {
                                    Value = tran.Value.X ,
                                    Frame = (float)tran.Time
                                });
                                con.TranslationY.KeyFrames.Add(new KeyFrame()
                                {
                                    Value = tran.Value.Y ,
                                    Frame = (float)tran.Time
                                });
                                con.TranslationZ.KeyFrames.Add(new KeyFrame()
                                {
                                    Value = tran.Value.Z ,
                                    Frame = (float)tran.Time
                                });
                            }
                        }

                        //Rotation XYZ
                        if (e.RotationKeys.TrueForAll(i => i.Value.Equals(e.RotationKeys.First().Value)))
                        {
                            con.RotationX.KeyFrames.Add(new KeyFrame());
                            con.RotationY.KeyFrames.Add(new KeyFrame());
                            con.RotationZ.KeyFrames.Add(new KeyFrame());
                        }
                        else
                        {
                            con.RotationX.CurveIndex = a.CurvesCount;
                            con.RotationY.CurveIndex = (ushort)(a.CurvesCount + 1);
                            con.RotationZ.CurveIndex = (ushort)(a.CurvesCount + 2);
                            a.CurvesCount += 3;

                            foreach (var rot in e.RotationKeys)
                            {
                                con.RotationX.KeyFrames.Add(new KeyFrame()
                                {
                                    Value = rot.Value.X,
                                    Frame = (float)rot.Time
                                });
                                con.RotationY.KeyFrames.Add(new KeyFrame()
                                {
                                    Value = rot.Value.Y,
                                    Frame = (float)rot.Time
                                });
                                con.RotationZ.KeyFrames.Add(new KeyFrame()
                                {
                                    Value = rot.Value.Z,
                                    Frame = (float)rot.Time
                                });
                            }
                        }

                        //Scale XYZ
                        if (e.ScalingKeys.TrueForAll(i => i.Value.Equals(e.ScalingKeys.First().Value)))
                        {

                            con.ScaleX.KeyFrames.Add(new KeyFrame() { Value = 1 });
                            con.ScaleY.KeyFrames.Add(new KeyFrame() { Value = 1 });
                            con.ScaleZ.KeyFrames.Add(new KeyFrame() { Value = 1 });
                        }
                        else
                        {
                            con.ScaleX.CurveIndex = a.CurvesCount;
                            con.ScaleY.CurveIndex = (ushort)(a.CurvesCount + 1);
                            con.ScaleZ.CurveIndex = (ushort)(a.CurvesCount + 2);
                            a.CurvesCount += 3;

                            foreach (var scale in e.ScalingKeys)
                            {
                                con.ScaleX.KeyFrames.Add(new KeyFrame(){
                                    Value = scale.Value.X,
                                    Frame = (float)scale.Time
                                });
                                con.ScaleY.KeyFrames.Add(new KeyFrame(){
                                    Value = scale.Value.Y,
                                    Frame = (float)scale.Time
                                });
                                con.ScaleZ.KeyFrames.Add(new KeyFrame(){
                                    Value = scale.Value.Z,
                                    Frame = (float)scale.Time
                                });
                            }
                        }


                        UsedNames.Add(e.NodeName);
                        element.Content = con;
                        a.Elements.Add(element);
                    }
                }
            }

            return a;
        }

        private static void SetAnimTransforms(H3DAnimTransform a, float EndFrame)
        {
            a.RotationX.InterpolationType = H3DInterpolationType.Linear;
            a.RotationX.Quantization = KeyFrameQuantization.StepLinear32;
            a.RotationY.InterpolationType = H3DInterpolationType.Linear;
            a.RotationY.Quantization = KeyFrameQuantization.StepLinear32;
            a.RotationZ.InterpolationType = H3DInterpolationType.Linear;
            a.RotationZ.Quantization = KeyFrameQuantization.StepLinear32;

            a.RotationX.EndFrame = EndFrame;
            a.RotationY.EndFrame = EndFrame;
            a.RotationZ.EndFrame = EndFrame;

            a.TranslationX.InterpolationType = H3DInterpolationType.Linear;
            a.TranslationX.Quantization = KeyFrameQuantization.StepLinear32;
            a.TranslationY.InterpolationType = H3DInterpolationType.Linear;
            a.TranslationY.Quantization = KeyFrameQuantization.StepLinear32;
            a.TranslationZ.InterpolationType = H3DInterpolationType.Linear;
            a.TranslationZ.Quantization = KeyFrameQuantization.StepLinear32;

            a.TranslationX.EndFrame = EndFrame;
            a.TranslationY.EndFrame = EndFrame;
            a.TranslationZ.EndFrame = EndFrame;

            a.ScaleX.InterpolationType = H3DInterpolationType.Linear;
            a.ScaleX.Quantization = KeyFrameQuantization.StepLinear32;
            a.ScaleY.InterpolationType = H3DInterpolationType.Linear;
            a.ScaleY.Quantization = KeyFrameQuantization.StepLinear32;
            a.ScaleZ.InterpolationType = H3DInterpolationType.Linear;
            a.ScaleZ.Quantization = KeyFrameQuantization.StepLinear32;

            a.ScaleX.EndFrame = EndFrame;
            a.ScaleY.EndFrame = EndFrame;
            a.ScaleZ.EndFrame = EndFrame;
        }
        #endregion

        //System.Drawing.Bitmap doesn't support 32 bit bitmaps
        //So we have to use another method to get the alpha channel
        private static Bitmap GetBitmap32(string filename)
        {
            var bitmapImage = new Bitmap(filename);
            using (MemoryStream ms = new MemoryStream(File.ReadAllBytes(filename)))
            {
                using (bitmapImage = (Bitmap)Bitmap.FromStream(ms, true, false))
                {
                    if (Bitmap.GetPixelFormatSize(bitmapImage.PixelFormat) == 32)
                    {
                        // Allocate the destination bitmap in ARGB format
                        Bitmap bitmapARGBImage = new Bitmap(bitmapImage.Width, bitmapImage.Height, PixelFormat.Format32bppArgb);

                        // Lock the original bitmap's bits
                        BitmapData bmpData = bitmapImage.LockBits(new Rectangle(0, 0, bitmapImage.Width, bitmapImage.Height),
                                             ImageLockMode.WriteOnly, bitmapImage.PixelFormat);

                        // Declare an array to hold the bytes of the original bitmap
                        byte[] rgbValues = new byte[bmpData.Stride * bitmapImage.Height];
                        // Copy the RGB values into the array
                        // bmpData.Scan0 is the address of the first pixel data in the bitmap
                        Marshal.Copy(bmpData.Scan0, rgbValues, 0, bmpData.Stride * bitmapImage.Height);
                        // Unlock the bits from the system memory
                        bitmapImage.UnlockBits(bmpData);
                        // Release bitmap data
                        if (bmpData != null) bmpData = null;

                        // Lock the new memory bitmap with alphachannel bitmap's bits
                        bmpData = bitmapARGBImage.LockBits(new Rectangle(0, 0, bitmapARGBImage.Width, bitmapARGBImage.Height),
                                         ImageLockMode.WriteOnly, bitmapARGBImage.PixelFormat);
                        // Copy the RGB values of original bitmap back to the new bitmap with alpha channel
                        Marshal.Copy(rgbValues, 0, bmpData.Scan0, bmpData.Stride * bitmapARGBImage.Height);
                        // Unlock the bits from the system memory
                        bitmapARGBImage.UnlockBits(bmpData);

                        // Save the image to the memory stream and convert it to Png format
                        bitmapARGBImage.Save(ms, ImageFormat.Png);

                        return bitmapARGBImage;
                    }
                }
            }

            Bitmap Img = new Bitmap(filename);
            return new Bitmap(Img);
        }

        private static List<string> GetMeshNames(Node n, List<string> names)
        {
            if(n.HasChildren){
                foreach (var c in n.Children)
                    GetMeshNames(c, names);
            }

            if(n.HasMeshes){
                for (int i = 0; i < n.MeshIndices.Count; i++)
                    names.Add(n.Name);
            }

            return names;
        }

        private static OpenTK.Vector3 GetMeshCenter(Mesh mesh)
        {
            OpenTK.Vector3 Min = new OpenTK.Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            OpenTK.Vector3 Max = new OpenTK.Vector3(float.MinValue, float.MinValue, float.MinValue);
            OpenTK.Vector3 Center = new OpenTK.Vector3();

            foreach(var p in mesh.Vertices)
            {
                //Get Max
                if (p.X > Max.X)
                    Max.X = p.X;
                if (p.Y > Max.Y)
                    Max.Y = p.Y;
                if (p.Z > Max.Z)
                    Max.Z = p.Z;

                //Get Min
                if (p.X < Min.X)
                    Min.X = p.X;
                if (p.Y < Min.Y)
                    Min.Y = p.Y ;
                if (p.Z < Min.Z)
                    Min.Z = p.Z;
            }

            Center.X = Min.X + (Max.X - Min.X) / 2;
            Center.Y = Min.Y + (Max.Y - Min.Y) / 2;
            Center.Z = Min.Z + (Max.Z - Min.Z) / 2;

            return Center;
        }
    }
}
