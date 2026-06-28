using FSO.Common;
using FSO.Common.Utils;
using FSO.Files;
using FSO.Files.Formats.IFF.Chunks;
using FSO.Files.RC;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FSO.Content
{
    public class RCMeshProvider
    {
        public GraphicsDevice GD;
        public HashSet<string> CacheFiles;
        public HashSet<string> ReplaceFiles;

        public RCMeshProvider(GraphicsDevice gd)
        {
            GD = gd;
            DGRP3DGeometry.ReplTextureProvider = GetTex;

            var repldir = Path.Combine(FSOEnvironment.ContentDir, "MeshReplace/");
            var userrepl = Path.Combine(FSOEnvironment.UserDir, "MeshReplace/");
            if (Directory.Exists(userrepl)) repldir = userrepl;
            var dir = Path.Combine(FSOEnvironment.UserDir, "MeshCache/");
            try
            {
                Directory.CreateDirectory(dir);
                Directory.CreateDirectory(repldir);
            } catch
            {
            }
            CacheFiles = new HashSet<string>(Directory.GetFiles(dir).Select(x => Path.GetFileName(x).ToLowerInvariant()));
            ReplaceFiles = new HashSet<string>(Directory.GetFiles(repldir).Select(x => Path.GetFileName(x).ToLowerInvariant()));
        }
        public Dictionary<DGRP, DGRP3DMesh> Cache = new Dictionary<DGRP, DGRP3DMesh>();
        public HashSet<DGRP> IgnoreRCCache = new HashSet<DGRP>();
        public ConcurrentDictionary<string, MTEX> ReplacementTex = new ConcurrentDictionary<string, MTEX>();
        public Dictionary<string, DGRP3DMesh> NameCache = new Dictionary<string, DGRP3DMesh>();

        public DGRP3DMesh Get(DGRP dgrp, OBJD obj)
        {
            DGRP3DMesh result = null;
            var repldir = Path.Combine(FSOEnvironment.ContentDir, "MeshReplace/");
            var dir = Path.Combine(FSOEnvironment.UserDir, "MeshCache/");
            if (!Cache.TryGetValue(dgrp, out result))
            {
                //does it exist in replacements
                var name = obj.ChunkParent.Filename.Replace('.', '_').ToLowerInvariant() + "_" + dgrp.ChunkID + ".fsom";
                if (ReplaceFiles.Contains(name))
                {
                    try
                    {
                        result = new DGRP3DMesh(dgrp, Path.Combine(repldir, name), GD);
                    }
                    catch (Exception)
                    {
                        result = null;
                    }
                }

                if (result == null)
                {
                    //does it exist in iff
                    try
                    {
                        result = dgrp.ChunkParent.Get<FSOM>(dgrp.ChunkID)?.Get(dgrp, GD);
                    }
                    catch (Exception)
                    {
                        result = null;
                    }
                }

                if (CacheFiles.Contains(name))
                {
                    if (result == null && !IgnoreRCCache.Contains(dgrp))
                    {
                        //does it exist in rc cache — but only if it's current. An outdated cache (older
                        //ReconstructVersion) makes LoadData throw; in the async streaming path that can't be
                        //recovered (it crashed the client), and even synchronously it would just blank the
                        //object. Skipping it here leaves result == null so the "create it anew" path below
                        //regenerates the mesh and overwrites the stale cache.
                        var cachePath = Path.Combine(dir, name);
                        if (DGRP3DMesh.FileMeshCurrent(cachePath))
                        {
                            try
                            {
                                result = new DGRP3DMesh(dgrp, cachePath, GD);
                            }
                            catch (Exception)
                            {
                                result = null;
                            }
                        }
                    }
                } else
                {

                }

                //create it anew
                if (result == null)
                {
                    result = new DGRP3DMesh(dgrp, obj, GD, dir);
                    CacheFiles.Add(name);
                }
                Cache[dgrp] = result;
            }
            return result;
        }

        public DGRP3DMesh Get(string name)
        {
            DGRP3DMesh result = null;
            var repldir = Path.Combine(FSOEnvironment.ContentDir, "3D/");
            if (!NameCache.TryGetValue(name, out result))
            {
                //does it exist in replacements
                try
                {
                    result = new DGRP3DMesh(null, Path.Combine(repldir, name), GD);
                }
                catch (Exception)
                {
                    result = null;
                }
                NameCache[name] = result;
            }
            return result;
        }

        public void ClearCache(DGRP dgrp)
        {
            //todo: dispose old?
            IgnoreRCCache.Add(dgrp);
            Cache.Remove(dgrp);
        }

        public void Replace(DGRP dgrp, DGRP3DMesh mesh)
        {
            //todo: dispose old?

            var name = dgrp.ChunkParent.Filename.Replace('.', '_').ToLowerInvariant() + "_" + dgrp.ChunkID + ".fsom";
            var repldir = Path.Combine(FSOEnvironment.ContentDir, "MeshReplace/");
            ReplaceFiles.Add(name);
            mesh.SaveDirectory = repldir;
            mesh.Save();

            Cache[dgrp] = mesh;
        }

        public DGRP3DTextureSource? GetTex(string name)
        {
            MTEX result = null;
            // TODO: Could have load the same texture multiple times due to a race condition?
            if (!ReplacementTex.TryGetValue(name, out result))
            {
                string dir;
                if (name.StartsWith("FSO_"))
                {
                    dir = Path.Combine(FSOEnvironment.ContentDir, "3D/");
                    name = name.Substring(4);
                }
                else dir = Path.Combine(FSOEnvironment.ContentDir, "MeshReplace/");
                //load from meshreplace folder
                try
                {
                    var path = Path.Combine(dir, name);

                    if (File.Exists(path))
                    {
                        result = new MTEX(File.OpenRead(path));
                    }
                }
                catch (Exception)
                {
                    result = null;
                }
                ReplacementTex[name] = result;
            }

            return DGRP3DTextureSource.WithDecoded(result, GD);
        }
    }
}
