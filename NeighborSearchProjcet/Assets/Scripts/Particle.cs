using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Runtime.InteropServices;

public class Particle : MonoBehaviour
{

    [SerializeField]
    private ParticleRenderer m_particleRenderer;
    [SerializeField]
    private Material m_instanceMaterial;


    [Space(20)]
    [SerializeField]
    private int m_instanceCount;
    [SerializeField]
    private ComputeShader m_particleCalculator;

    [Header("パーティクル設定")]
    [Space(20)]
    [SerializeField]
    private float m_particleRange;

    [SerializeField]
    private float m_scale;
    [SerializeField]
    private Color m_color;
    [SerializeField]
    private float m_particleVelocity;
    [SerializeField]
    private float m_searchRange;
    [SerializeField]
    private int m_searchIndex;

    private int m_gridRange;

    private int m_calcParticlePositionKernel;
    private int m_initializeGridInfoKernel;
    private int m_calcParticleGridKernel;
    private int m_buildGridIndexRangeKernel;
    private int m_rearrengeBufferKernel;
    private int m_copyBufferKernel;
    private int m_searchNeighborKernel;
    private int m_bitonicSortKernel;
    
    private ComputeBuffer m_particleBuffer;
    private ComputeBuffer m_particleFieldBuffer;
    private ComputeBuffer m_gridInfoBuffer;
    private ComputeBuffer m_sortedParticleBuffer;
    
    private Vector3Int m_gridGroupSize;
    private Vector3Int m_particleGroupSize;

    private readonly int m_NumThreads = 64;

    private readonly int m_DeltaTimePropId = Shader.PropertyToID("_DeltaTime");
    private readonly int m_swapBitPropId = Shader.PropertyToID("_SwapBit");
    private readonly int m_upperBitPropId = Shader.PropertyToID("_UpperBit");

    /// <summary>
    /// ComputeBufferをReleaseする用
    /// </summary>
    private System.Action m_releaseBufferAction;

    struct ParticleInfo {
        public Vector3 position;
        public Vector4 color;
        public float scale;
        public Vector3 velocity;
    }

    // Start is called before the first frame update
    void Start()
    {
        m_instanceCount = Mathf.ClosestPowerOfTwo(m_instanceCount);
        m_particleGroupSize = new Vector3Int(m_instanceCount/m_NumThreads, 1, 1);
        m_particleRenderer.InitializeParticleRenderer(m_instanceCount, m_instanceMaterial);

        // マス目の個数を計算
        // Mathf.CeilToInt:引数以上の整数を返す
        // 参考 : https://docs.unity3d.com/ja/current/ScriptReference/Mathf.CeilToInt.html
        m_gridRange = Mathf.CeilToInt((m_particleRange * 2) / m_searchRange);
        m_particleCalculator.SetInt("_GridTotalRange", m_gridRange*m_gridRange*m_gridRange);

        InitializeParticleBuffer();
        InitializeParticleFieldBuffer();
        InitializeGridInfoBuffer();
        InitializeSortedParticleBuffer();

        SetUpCalcParticlePosition();
        SetUpCalcParticleGrid();
        SetUpBitonicSort();
        SetUpInitializeGridInfo();
        SetUpBuildGridIndexRange();
        SetUpSearchNeighbor();
        SetUpRearrengeBuffer();
        SetUpCopyBuffer();
    }


    void Update() {

        // パーティクルのGridIndexを求める
        m_particleCalculator.Dispatch(m_calcParticleGridKernel,
                                     m_particleGroupSize.x,
                                     m_particleGroupSize.y,
                                     m_particleGroupSize.z);

        // GridIndexでソート
        BitonicSort();

        // Grid情報を初期化
        m_particleCalculator.Dispatch(m_initializeGridInfoKernel,
                                     m_gridGroupSize.x,
                                     m_gridGroupSize.y,
                                     m_gridGroupSize.z);


        // Grid内にあるパーティクルのbeginとendを求める
        m_particleCalculator.Dispatch(m_buildGridIndexRangeKernel,
                                     m_particleGroupSize.x,
                                     m_particleGroupSize.y,
                                     m_particleGroupSize.z);

        // _SortedParticleBufferにGridIndex順でパーティクル情報を移す
        m_particleCalculator.Dispatch(m_rearrengeBufferKernel,
                                     m_particleGroupSize.x,
                                     m_particleGroupSize.y,
                                     m_particleGroupSize.z);

        // m_SortedParticleBufferをm_ParticleBufferに移す
        m_particleCalculator.Dispatch(m_copyBufferKernel,
                                     m_particleGroupSize.x,
                                     m_particleGroupSize.y,
                                     m_particleGroupSize.z);


        // 近傍探索
        m_particleCalculator.SetInt("_SearchIndex", m_searchIndex);
        m_particleCalculator.Dispatch(m_searchNeighborKernel,
                                     m_particleGroupSize.x,
                                     m_particleGroupSize.y,
                                     m_particleGroupSize.z);

        // パーティクルの移動
        m_particleCalculator.SetFloat(m_DeltaTimePropId, Time.deltaTime);
        m_particleCalculator.Dispatch(m_calcParticlePositionKernel,
                                     m_particleGroupSize.x,
                                     m_particleGroupSize.y,
                                     m_particleGroupSize.z);


    }


    private void InitializeParticleBuffer() {

        ParticleInfo[] particles = new ParticleInfo[m_instanceCount];

        for(int i = 0; i < m_instanceCount; ++i) {
            particles[i].position = RandomVector(-m_particleRange, m_particleRange);
            particles[i].color = m_color;
            particles[i].scale = m_scale;
            // Random.onUnitySphere:半径1の球面上のランダムな点を返す
            // つまり、大きさm_particleVelocityのランダムなベクトルを計算
            particles[i].velocity = Random.onUnitSphere * m_particleVelocity;
        }

        m_particleBuffer = new ComputeBuffer(m_instanceCount, Marshal.SizeOf(typeof(ParticleInfo)));
        m_releaseBufferAction += () => m_particleBuffer?.Release();
        m_particleBuffer.SetData(particles);

        m_instanceMaterial.SetBuffer("_ParticleBuffer", m_particleBuffer);
    }


    private void InitializeParticleFieldBuffer() {

        m_particleFieldBuffer = new ComputeBuffer(m_instanceCount, Marshal.SizeOf(typeof(uint))*2);
        m_releaseBufferAction += () => m_particleFieldBuffer?.Release();

    }


    private void InitializeGridInfoBuffer() {

        // Gridはマス目の個数で初期化する
        m_gridInfoBuffer = new ComputeBuffer(m_gridRange*m_gridRange*m_gridRange, Marshal.SizeOf(typeof(uint))*2);
        m_releaseBufferAction += () => m_gridInfoBuffer?.Release();

    }

    private void InitializeSortedParticleBuffer() {

        m_sortedParticleBuffer = new ComputeBuffer(m_instanceCount, Marshal.SizeOf(typeof(ParticleInfo)));
        m_releaseBufferAction += () => m_sortedParticleBuffer?.Release();

    }

    private void SetUpCalcParticlePosition() {

        m_calcParticlePositionKernel = m_particleCalculator.FindKernel("CalcParticlePosition");

        m_particleCalculator.GetKernelThreadGroupSizes(m_calcParticlePositionKernel,
                                                      out uint x,
                                                      out uint y,
                                                      out uint z);

        m_particleCalculator.SetFloat("_ParticleRange", m_particleRange);
        m_particleCalculator.SetBuffer(m_calcParticlePositionKernel, "_Particle", m_particleBuffer);


    }


    private void SetUpCalcParticleGrid() {


        m_particleCalculator.SetInt("_GridRange", m_gridRange);
        float minGrid = -m_particleRange;
        m_particleCalculator.SetFloat("_MinGrid", minGrid);
        m_particleCalculator.SetFloat("_GridSize", m_searchRange);

        m_calcParticleGridKernel = m_particleCalculator.FindKernel("CalcParticleGrid");

        m_particleCalculator.SetBuffer(m_calcParticleGridKernel, 
                                      "_Particle", 
                                      m_particleBuffer);

        m_particleCalculator.SetBuffer(m_calcParticleGridKernel, 
                                      "_ParticleField", 
                                      m_particleFieldBuffer);

    }


    private void SetUpBitonicSort() {

        m_bitonicSortKernel = m_particleCalculator.FindKernel("ParallelBitonic");
        m_particleCalculator.SetBuffer(m_bitonicSortKernel,
                                      "_ParticleField", 
                                      m_particleFieldBuffer);

    }


    private void SetUpInitializeGridInfo() {

        int totalGridRange = m_gridRange*m_gridRange*m_gridRange;
        m_initializeGridInfoKernel = m_particleCalculator.FindKernel("InitializeGridInfo");
        m_particleCalculator.GetKernelThreadGroupSizes(m_initializeGridInfoKernel, out uint x, out uint y, out uint z);
        m_gridGroupSize = new Vector3Int(Mathf.CeilToInt((float)totalGridRange/x), (int)y, (int)z);

        m_particleCalculator.SetBuffer(m_initializeGridInfoKernel, "_GridInfo", m_gridInfoBuffer);
        
    }


    private void SetUpBuildGridIndexRange() {

        m_buildGridIndexRangeKernel = m_particleCalculator.FindKernel("BuildGridIndexRange");
        m_particleCalculator.SetBuffer(m_buildGridIndexRangeKernel,
                                      "_ParticleField",
                                      m_particleFieldBuffer);
        m_particleCalculator.SetBuffer(m_buildGridIndexRangeKernel,
                                      "_GridInfo",
                                      m_gridInfoBuffer);
        m_particleCalculator.SetInt("_ParticleCount", m_instanceCount);
    }


    private void SetUpSearchNeighbor() {

        m_searchNeighborKernel = m_particleCalculator.FindKernel("SearchNeighbor");
        m_particleCalculator.SetBuffer(m_searchNeighborKernel,
                                      "_Particle",
                                      m_particleBuffer);
        m_particleCalculator.SetBuffer(m_searchNeighborKernel,
                                      "_GridInfo",
                                      m_gridInfoBuffer);
        m_particleCalculator.SetBuffer(m_searchNeighborKernel,
                                      "_ParticleField",
                                      m_particleFieldBuffer);
        m_particleCalculator.SetBuffer(m_searchNeighborKernel,
                                      "_SortedParticle",
                                      m_sortedParticleBuffer);
        m_particleCalculator.SetFloat("_SearchRange", m_searchRange);
        m_particleCalculator.SetInt("_SearchIndex", m_searchIndex);
    }

    private void SetUpRearrengeBuffer() {

        m_rearrengeBufferKernel = m_particleCalculator.FindKernel("RearrengeBuffer");
        m_particleCalculator.SetBuffer(m_rearrengeBufferKernel,
                                      "_ParticleField",
                                      m_particleFieldBuffer);
        m_particleCalculator.SetBuffer(m_rearrengeBufferKernel, 
                                      "_SortedParticle", 
                                      m_sortedParticleBuffer);
        m_particleCalculator.SetBuffer(m_rearrengeBufferKernel, 
                                      "_Particle", 
                                      m_particleBuffer);

    }


    private void SetUpCopyBuffer() {

        m_copyBufferKernel = m_particleCalculator.FindKernel("CopyBuffer");
        m_particleCalculator.SetBuffer(m_copyBufferKernel,
                                      "_Particle",
                                      m_particleBuffer);
        m_particleCalculator.SetBuffer(m_copyBufferKernel,
                                      "_SortedParticle",
                                      m_sortedParticleBuffer);

    }

    private void BitonicSort() {

        int logCount = (int)Mathf.Log(m_instanceCount, 2);

        for(int i = 0; i < logCount; ++i) {

            for(int j = 0; j <= i; j++) {

                int swapBit = 1 << (i - j);
                int upperBit = 2 << i;

                m_particleCalculator.SetInt(m_swapBitPropId, swapBit);
                m_particleCalculator.SetInt(m_upperBitPropId, upperBit);
                m_particleCalculator.Dispatch(m_bitonicSortKernel,
                                             m_particleGroupSize.x,
                                             m_particleGroupSize.y,
                                             m_particleGroupSize.z);
            }
        }
    }


    private Vector3 RandomVector(float min, float max) {

        return new Vector3(
            Random.Range(min, max),
            Random.Range(min, max),
            Random.Range(min, max)
            );

    }

    // 領域の解放
    private void OnDisable() {

        m_releaseBufferAction();

    }


}