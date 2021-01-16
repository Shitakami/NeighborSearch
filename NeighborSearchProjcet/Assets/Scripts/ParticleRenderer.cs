using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public class ParticleRenderer : MonoBehaviour
{

    [Header("DrawMeshInstancedIndirectのパラメータ")]
    [SerializeField]
    private Mesh m_mesh;

    [SerializeField]
    private Bounds m_bounds;

    [SerializeField]
    private ShadowCastingMode m_shadowCastingMode;

    [SerializeField]
    private bool m_receiveShadows;

    private ComputeBuffer m_argsBuffer;

    private Material m_instanceMaterial;

    public void InitializeParticleRenderer(int instanceCount, Material instanceMaterial) {

        uint[] args = new uint[5] { 0, 0, 0, 0, 0 };

        uint numIndices = (m_mesh != null) ? (uint) m_mesh.GetIndexCount(0) : 0;

        args[0] = numIndices;
        args[1] = (uint)instanceCount;
        
        m_argsBuffer = new ComputeBuffer(1, args.Length * sizeof(uint), ComputeBufferType.IndirectArguments);

        m_argsBuffer.SetData(args);
        m_instanceMaterial = instanceMaterial;
    }


    // Update is called once per frame
    void LateUpdate()
    {

        Graphics.DrawMeshInstancedIndirect(
            m_mesh,
            0,
            m_instanceMaterial,
            m_bounds,
            m_argsBuffer,
            0,
            null,
            m_shadowCastingMode,
            m_receiveShadows
        );

    }


    private void OnDestroy() {

        m_argsBuffer?.Release();

    }

}
