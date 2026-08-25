using System;
using System.Collections.Generic;
using UnityEngine;

namespace Snowfield.Sculpture
{
    /// <summary>
    /// Serializable snapshot of one sculpture (or loose snowball). Local save files are JSON of this;
    /// Phase 3 uploads the same shape (densityB64 is the RLE blob from GridSerializer).
    /// </summary>
    [Serializable]
    public class SculptureRecord
    {
        public Vector3 position;
        public Quaternion rotation;
        public int gridSize;
        public float voxelSize;
        public Vector3 gridOffset;
        public bool isSnowball;
        public float snowballRadius;
        public bool isLoose;
        public string densityB64;
        public List<PropRecord> props = new List<PropRecord>();
    }

    [Serializable]
    public class PropRecord
    {
        public string prefabId;
        public Vector3 localPos;
        public Quaternion localRot;
    }

    /// <summary>JsonUtility can't serialize a bare list, so files wrap one record.</summary>
    [Serializable]
    public class SculptureFile
    {
        public SculptureRecord sculpture;
    }
}
