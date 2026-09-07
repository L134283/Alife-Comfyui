using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Platform;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using AntDesign;

namespace Alife.Plugin.Comfyui;

public partial class ComfyuiServiceUI : ModuleUIBase<ComfyuiService, ComfyuiConfig>
{
    string? detectMessage; string? _scanDetectMessage;
    string _scanDir = "";
    List<string> _scannedWorkflows = new();
    bool _showWorkflowDropdown;
    List<(string NodeId, string ClassType, string KeyParams)> _nodeOverview = new();
    string? _nodeOverviewError;

    // 命名工作流卡片管理
    List<WorkflowCard> _workflowCards = new();
    int _activeScanCardIndex = -1; // 当前展开浏览下拉的卡片索引

    // 提示词预设卡片管理
    List<PresetCard> _presetCards = new();
    int _activePresetIndex = -1; // 当前展开编辑的预设索引
    string _newPresetName = "";
    string _newPresetContent = "";

    // 在线标签检索连通测试
    string? _danbooruTestMessage;
    bool _danbooruTesting;

    // 在线角色检索（AnimaDex）连通测试
    string? _animadexTestMessage;
    bool _animadexTesting;

    // 模型卸载与空闲自动释放
    bool _unloadingModel;
    string? _unloadMessage;

    record WorkflowCard
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public string Prefix { get; set; } = "";
    }

    record PresetCard
    {
        public string Name { get; set; } = "";
        public string Content { get; set; } = "";
    }

    const string Css = @"
/* ========== 根：旋转霓虹描边 ========== */
.cfy-root {
    --pink: #ec4899;
    --pink-hot: #f472b6;
    --rose: #fb7185;
    --blush: #fbcfe8;
    --cream: #fff7fb;
    --ink: #5b2145;
    --ink-soft: #9d4b74;
    position: relative;
    width: 100%;
    box-sizing: border-box;
    border-radius: 26px;
    padding: 3px;
    isolation: isolate;
    overflow: hidden;
}
.cfy-root::before {
    content: '';
    position: absolute;
    inset: -40%;
    z-index: -2;
    background: conic-gradient(
        from var(--cfy-angle, 0deg),
        #ff6bb5, #ff9ad5, #ffd0e8, #fff, #fda4af,
        #f472b6, #e879f9, #c084fc, #f472b6, #ff6bb5
    );
    filter: blur(0px);
}
.cfy-root::after {
    content: '';
    position: absolute;
    inset: 0;
    z-index: -1;
    border-radius: 26px;
    background: linear-gradient(135deg, #fff9fc, #ffe4ef);
    box-shadow:
        0 0 40px rgba(236,72,153,0.35),
        0 0 80px rgba(244,114,182,0.2),
        0 25px 60px rgba(190,24,93,0.18),
        inset 0 1px 0 rgba(255,255,255,0.9);
}
@property --cfy-angle {
    syntax: '<angle>';
    initial-value: 0deg;
    inherits: false;
}
@keyframes cfy-spin {
    to { --cfy-angle: 360deg; transform: rotate(360deg); }
}

/* ========== 主容器 ========== */
.cfy-container {
    position: relative;
    width: 100%;
    box-sizing: border-box;
    border-radius: 23px;
    padding: 30px 32px 24px;
    color: var(--ink);
    overflow: hidden;
    background:
        radial-gradient(ellipse 90% 60% at 0% 0%, rgba(255,154,198,0.45), transparent 55%),
        radial-gradient(ellipse 80% 50% at 100% 0%, rgba(253,164,175,0.35), transparent 50%),
        radial-gradient(ellipse 70% 50% at 50% 100%, rgba(232,121,249,0.18), transparent 55%),
        linear-gradient(165deg, #fffafc 0%, #fff0f6 45%, #ffe8f1 100%);
}

/* 极光层 */
.cfy-aurora {
    position: absolute;
    inset: -20%;
    z-index: 0;
    pointer-events: none;
    background:
        linear-gradient(115deg,
            transparent 20%,
            rgba(244,114,182,0.18) 35%,
            rgba(232,121,249,0.14) 45%,
            rgba(251,113,133,0.16) 55%,
            transparent 70%);
    background-size: 200% 200%;
    mix-blend-mode: multiply;
    filter: blur(8px);
}
@keyframes cfy-aurora {
    0% { background-position: 0% 40%; transform: rotate(-2deg) scale(1.05); }
    50% { background-position: 80% 60%; transform: rotate(1deg) scale(1.1); }
    100% { background-position: 100% 30%; transform: rotate(-1deg) scale(1.05); }
}

/* 网格 */
.cfy-grid-bg {
    position: absolute;
    inset: 0;
    z-index: 0;
    pointer-events: none;
    background-image:
        linear-gradient(rgba(244,114,182,0.06) 1px, transparent 1px),
        linear-gradient(90deg, rgba(244,114,182,0.06) 1px, transparent 1px);
    background-size: 28px 28px;
    mask-image: radial-gradient(ellipse 80% 70% at 50% 40%, #000 20%, transparent 75%);
}
@keyframes cfy-grid-drift {
    from { background-position: 0 0; }
    to { background-position: 28px 28px; }
}

/* 光球 */
.cfy-orb {
    position: absolute;
    border-radius: 50%;
    pointer-events: none;
    z-index: 0;
    filter: blur(1px);
    will-change: transform;
}
.cfy-orb-1 {
    width: 280px; height: 280px;
    top: -90px; right: -70px;
    background: radial-gradient(circle, rgba(255,120,190,0.75) 0%, rgba(255,120,190,0) 68%);
}
.cfy-orb-2 {
    width: 220px; height: 220px;
    bottom: 20px; left: -70px;
    background: radial-gradient(circle, rgba(253,164,175,0.65) 0%, rgba(253,164,175,0) 68%);
}
.cfy-orb-3 {
    width: 160px; height: 160px;
    top: 40%; left: 55%;
    background: radial-gradient(circle, rgba(232,121,249,0.4) 0%, rgba(232,121,249,0) 70%);
}
.cfy-orb-4 {
    width: 100px; height: 100px;
    top: 15%; left: 20%;
    background: radial-gradient(circle, rgba(255,255,255,0.7) 0%, rgba(255,182,213,0.3) 40%, transparent 70%);
}
@keyframes cfy-orb-a {
    0%,100% { transform: translate(0,0) scale(1); }
    33% { transform: translate(-30px, 40px) scale(1.15); }
    66% { transform: translate(-10px, 15px) scale(0.92); }
}
@keyframes cfy-orb-b {
    0%,100% { transform: translate(0,0) scale(1); }
    50% { transform: translate(35px, -30px) scale(1.2); }
}
@keyframes cfy-orb-c {
    0%,100% { transform: translate(0,0) scale(1) rotate(0deg); }
    50% { transform: translate(-40px, -25px) scale(1.25) rotate(40deg); }
}
@keyframes cfy-orb-d {
    0%,100% { transform: translate(0,0) scale(1); opacity: 0.6; }
    50% { transform: translate(20px, 30px) scale(1.4); opacity: 1; }
}

/* 粒子星场 */
.cfy-particle {
    position: absolute;
    border-radius: 50%;
    pointer-events: none;
    z-index: 0;
    background: #fff;
    box-shadow: 0 0 6px 1px rgba(255,182,213,0.95), 0 0 14px rgba(236,72,153,0.5);
    opacity: 0.85;
}
@keyframes cfy-particle-float {
    0% { transform: translateY(20px) scale(0.4); opacity: 0; }
    15% { opacity: 1; }
    85% { opacity: 0.85; }
    100% { transform: translateY(-420px) scale(1.2); opacity: 0; }
}
.cfy-p1  { width:5px; height:5px; left:6%;  bottom:5%;  animation-duration: 7s;  animation-delay: 0s; }
.cfy-p2  { width:3px; height:3px; left:14%; bottom:0%;  animation-duration: 9s;  animation-delay: 1.2s; }
.cfy-p3  { width:4px; height:4px; left:22%; bottom:8%;  animation-duration: 6.5s; animation-delay: 0.4s; }
.cfy-p4  { width:6px; height:6px; left:35%; bottom:2%;  animation-duration: 8s;  animation-delay: 2s; }
.cfy-p5  { width:3px; height:3px; left:48%; bottom:10%; animation-duration: 10s; animation-delay: 0.8s; }
.cfy-p6  { width:5px; height:5px; left:58%; bottom:0%;  animation-duration: 7.5s; animation-delay: 1.6s; }
.cfy-p7  { width:4px; height:4px; left:68%; bottom:6%;  animation-duration: 9.5s; animation-delay: 0.2s; }
.cfy-p8  { width:3px; height:3px; left:78%; bottom:3%;  animation-duration: 6s;  animation-delay: 2.4s; }
.cfy-p9  { width:5px; height:5px; left:88%; bottom:9%;  animation-duration: 8.5s; animation-delay: 1s; }
.cfy-p10 { width:4px; height:4px; left:42%; bottom:4%;  animation-duration: 11s; animation-delay: 3s; }
.cfy-p11 { width:3px; height:3px; left:92%; bottom:1%;  animation-duration: 7s;  animation-delay: 1.8s; }
.cfy-p12 { width:6px; height:6px; left:28%; bottom:7%;  animation-duration: 9s;  animation-delay: 2.8s; }

/* 闪星 */
.cfy-star {
    position: absolute;
    width: 10px; height: 10px;
    z-index: 0;
    pointer-events: none;
    background: radial-gradient(circle, #fff 0%, #ffc0e0 40%, transparent 70%);
    opacity: 0.75;
    clip-path: polygon(50% 0%, 61% 35%, 98% 35%, 68% 57%, 79% 91%, 50% 70%, 21% 91%, 32% 57%, 2% 35%, 39% 35%);
}
.cfy-star-1 { top: 8%;  left: 12%; animation-delay: 0s; }
.cfy-star-2 { top: 18%; right: 15%; animation-delay: 0.6s; width: 8px; height: 8px; }
.cfy-star-3 { top: 55%; left: 8%;  animation-delay: 1.1s; width: 7px; height: 7px; }
.cfy-star-4 { top: 70%; right: 10%; animation-delay: 1.7s; width: 12px; height: 12px; }
.cfy-star-5 { top: 30%; left: 50%; animation-delay: 0.3s; width: 6px; height: 6px; }
@keyframes cfy-star-twinkle {
    0%,100% { opacity: 0.15; transform: scale(0.5) rotate(0deg); }
    50% { opacity: 1; transform: scale(1.4) rotate(20deg); filter: drop-shadow(0 0 6px #fff); }
}

/* 内容层 */
.cfy-content { position: relative; z-index: 2; }

/* 错落入场 */
@keyframes cfy-rise {
    from { opacity: 0; transform: translateY(14px); }
    to { opacity: 1; transform: translateY(0); }
}

/* ========== Hero ========== */
.cfy-hero {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    gap: 16px;
    margin-bottom: 20px;
    flex-wrap: wrap;
}
.cfy-title-wrap { flex: 1; min-width: 220px; }
.cfy-kicker {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    font-size: 11px;
    font-weight: 800;
    letter-spacing: 0.18em;
    text-transform: uppercase;
    color: #be185d;
    background: linear-gradient(90deg, rgba(255,255,255,0.95), rgba(255,228,240,0.8));
    border: 1px solid rgba(244,114,182,0.4);
    border-radius: 999px;
    padding: 4px 14px;
    margin-bottom: 10px;
    box-shadow: 0 4px 16px rgba(244,114,182,0.2);
    position: relative;
    overflow: hidden;
    animation: cfy-kicker-in 0.8s 0.1s cubic-bezier(.16,1,.3,1) both;
}
.cfy-kicker::before {
    content: '';
    position: absolute;
    inset: 0;
    background: linear-gradient(90deg, transparent, rgba(255,255,255,0.8), transparent);
    transform: translateX(-100%);
}
.cfy-kicker-dot {
    width: 7px; height: 7px;
    border-radius: 50%;
    background: #ec4899;
    box-shadow: 0 0 8px #f472b6;
}
@keyframes cfy-blink {
    0%,100% { opacity: 1; transform: scale(1); }
    50% { opacity: 0.4; transform: scale(0.7); }
}
@keyframes cfy-sheen {
    0%, 60% { transform: translateX(-100%); }
    100% { transform: translateX(200%); }
}
@keyframes cfy-kicker-in {
    from { opacity: 0; transform: translateX(-20px); }
    to { opacity: 1; transform: translateX(0); }
}

.cfy-title {
    font-size: 32px;
    font-weight: 900;
    line-height: 1.1;
    margin: 0 0 8px;
    letter-spacing: -0.02em;
    position: relative;
    display: inline-block;
    background: linear-gradient(
        100deg,
        #9d174d 0%,
        #be185d 15%,
        #ec4899 30%,
        #f472b6 45%,
        #fb7185 55%,
        #e879f9 70%,
        #f472b6 85%,
        #be185d 100%
    );
    background-size: 300% auto;
    -webkit-background-clip: text;
    background-clip: text;
    -webkit-text-fill-color: transparent;
    animation: cfy-title-pop 0.9s 0.15s cubic-bezier(.16,1,.3,1) both;
    filter: drop-shadow(0 4px 16px rgba(236,72,153,0.35));
}
@keyframes cfy-title-shimmer {
    0% { background-position: 0% center; }
    100% { background-position: 300% center; }
}
@keyframes cfy-title-glint {
    0%, 40% { background-position: -100% center; opacity: 0; }
    50% { opacity: 1; }
    100% { background-position: 200% center; opacity: 0; }
}
@keyframes cfy-title-pop {
    from { opacity: 0; transform: scale(0.85) translateY(12px); letter-spacing: 0.15em; }
    to { opacity: 1; transform: scale(1) translateY(0); letter-spacing: -0.02em; }
}

.cfy-subtitle {
    font-size: 13px;
    color: var(--ink-soft);
    line-height: 1.55;
    position: relative;
    display: inline-block;
    animation: cfy-sub-in 0.8s 0.35s both;
}
@keyframes cfy-cursor {
    0%,100% { opacity: 1; }
    50% { opacity: 0; }
}
@keyframes cfy-sub-in {
    from { opacity: 0; transform: translateY(8px); }
    to { opacity: 1; transform: translateY(0); }
}

/* 状态徽章 */
.cfy-badge-wrap {
    position: relative;
    display: inline-flex;
    align-items: center;
    justify-content: center;
}
.cfy-badge-ring {
    position: absolute;
    inset: -6px;
    border-radius: 999px;
    border: 2px solid rgba(236,72,153,0.45);
    pointer-events: none;
}
.cfy-badge-ring2 {
    position: absolute;
    inset: -12px;
    border-radius: 999px;
    border: 1.5px solid rgba(244,114,182,0.3);
    pointer-events: none;
}
@keyframes cfy-ring-pulse {
    0% { transform: scale(0.9); opacity: 0.9; }
    100% { transform: scale(1.35); opacity: 0; }
}
.cfy-badge-on, .cfy-badge-off {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    padding: 9px 18px;
    border-radius: 999px;
    font-size: 12.5px;
    font-weight: 800;
    white-space: nowrap;
    letter-spacing: 0.03em;
    position: relative;
    z-index: 1;
}
.cfy-badge-on {
    color: #fff;
    background: linear-gradient(135deg, #f9a8d4, #f472b6 40%, #ec4899 70%, #db2777);
    background-size: 200% 200%;
    box-shadow:
        0 6px 22px rgba(236,72,153,0.55),
        0 0 0 1px rgba(255,255,255,0.4) inset,
        0 0 30px rgba(244,114,182,0.4);
}
.cfy-badge-on::before {
    content: '';
    width: 9px; height: 9px;
    border-radius: 50%;
    background: #fff;
    box-shadow: 0 0 10px #fff, 0 0 18px #fbcfe8;
}
@keyframes cfy-badge-flow {
    0%,100% { background-position: 0% 50%; }
    50% { background-position: 100% 50%; }
}
@keyframes cfy-badge-glow {
    0%,100% { filter: brightness(1); }
    50% { filter: brightness(1.12); }
}
.cfy-badge-off {
    color: var(--ink-soft);
    background: rgba(255,255,255,0.75);
    border: 1.5px solid rgba(244,114,182,0.35);
    backdrop-filter: blur(8px);
}
.cfy-badge-off::before {
    content: '';
    width: 9px; height: 9px;
    border-radius: 50%;
    background: #f9a8d4;
}

/* 说明卡片 */
.cfy-alert {
    position: relative;
    background: linear-gradient(135deg, rgba(255,255,255,0.88), rgba(255,240,247,0.72));
    border: 1px solid rgba(244,114,182,0.3);
    border-radius: 18px;
    padding: 16px 20px;
    margin-bottom: 8px;
    backdrop-filter: blur(14px);
    box-shadow:
        0 10px 30px rgba(244,114,182,0.12),
        inset 0 1px 0 rgba(255,255,255,0.95);
    overflow: hidden;
    transition: transform 0.35s cubic-bezier(.16,1,.3,1), box-shadow 0.35s ease;
}
.cfy-alert:hover {
    transform: translateY(-2px);
    box-shadow: 0 16px 40px rgba(236,72,153,0.18);
}
.cfy-alert::before {
    content: '';
    position: absolute;
    left: 0; top: 0; bottom: 0;
    width: 5px;
    background: linear-gradient(180deg, #f472b6, #ec4899, #e879f9, #f472b6);
    background-size: 100% 200%;
    border-radius: 5px 0 0 5px;
}
.cfy-alert::after {
    content: '';
    position: absolute;
    top: -50%; right: -10%;
    width: 140px; height: 140px;
    border-radius: 50%;
    background: radial-gradient(circle, rgba(244,114,182,0.2), transparent 70%);
    pointer-events: none;
}
@keyframes cfy-bar-flow {
    0% { background-position: 0% 0%; }
    100% { background-position: 0% 200%; }
}
.cfy-alert-title {
    font-weight: 900;
    color: #be185d;
    margin-bottom: 8px;
    font-size: 14px;
    display: flex;
    align-items: center;
    gap: 8px;
}
.cfy-alert-title::before {
    content: '✦';
    display: inline-block;
    color: #f472b6;
    text-shadow: 0 0 10px rgba(244,114,182,0.8);
}
@keyframes cfy-spin-icon {
    to { transform: rotate(360deg); }
}
.cfy-alert-desc {
    font-size: 12.5px;
    color: #8b3a62;
    line-height: 1.8;
    white-space: pre-line;
}

/* 分区标题 */
.cfy-section {
    display: flex;
    align-items: center;
    gap: 12px;
    font-size: 15px;
    font-weight: 900;
    color: #be185d;
    margin: 26px 0 14px;
    letter-spacing: 0.03em;
    position: relative;
}
.cfy-section::before {
    content: '';
    width: 12px; height: 12px;
    border-radius: 50%;
    background: linear-gradient(135deg, #f472b6, #ec4899, #e879f9);
    background-size: 200% 200%;
    box-shadow: 0 0 14px rgba(236,72,153,0.85), 0 0 28px rgba(244,114,182,0.4);
    flex-shrink: 0;
}
.cfy-section::after {
    content: '';
    flex: 1;
    height: 2px;
    background: linear-gradient(90deg,
        rgba(244,114,182,0.7),
        rgba(232,121,249,0.4),
        rgba(251,207,232,0.15),
        transparent);
    border-radius: 2px;
    position: relative;
    overflow: hidden;
}
@keyframes cfy-dot-pulse {
    0%,100% { transform: scale(1); }
    50% { transform: scale(1.35); }
}

/* 标签 / 提示 */
.cfy-label {
    font-weight: 800;
    margin-bottom: 6px;
    margin-top: 12px;
    font-size: 12.5px;
    color: #9d174d;
    letter-spacing: 0.02em;
    transition: color 0.2s;
}
.cfy-hint {
    font-size: 11px;
    color: #b06a8c;
    margin: 5px 0 8px 2px;
    line-height: 1.65;
}

/* 输入框 */
.cfy-container .ant-input,
.cfy-container .cfy-textarea,
.cfy-container .cfy-select {
    border: 1.5px solid rgba(244,114,182,0.3) !important;
    border-radius: 14px !important;
    background: rgba(255,255,255,0.82) !important;
    color: var(--ink) !important;
    box-shadow: 0 2px 10px rgba(244,114,182,0.07);
    transition: all 0.3s cubic-bezier(.16,1,.3,1) !important;
    backdrop-filter: blur(6px);
}
.cfy-container .ant-input:hover,
.cfy-container .cfy-textarea:hover,
.cfy-container .cfy-select:hover {
    border-color: #f9a8d4 !important;
    background: rgba(255,255,255,0.96) !important;
    transform: translateY(-1px);
    box-shadow: 0 6px 18px rgba(244,114,182,0.14) !important;
}
.cfy-container .ant-input:focus,
.cfy-container .ant-input-focused,
.cfy-container .cfy-textarea:focus,
.cfy-container .cfy-select:focus {
    border-color: #ec4899 !important;
    box-shadow:
        0 0 0 4px rgba(236,72,153,0.18),
        0 8px 24px rgba(244,114,182,0.16) !important;
    background: #fff !important;
    transform: translateY(-1px);
}
.cfy-container .cfy-textarea {
    width: 100%;
    min-height: 96px;
    resize: vertical;
    font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
    font-size: 12px;
    line-height: 1.7;
    padding: 12px 14px;
    box-sizing: border-box;
}
.cfy-container .cfy-select {
    width: 100%;
    padding: 9px 14px;
    font-size: 13px;
    cursor: pointer;
    outline: none;
}
.cfy-container .cfy-select option {
    background: #fff;
    color: var(--ink);
}

/* 分辨率卡片 — 全息 */
.cfy-reso-grid {
    display: grid;
    grid-template-columns: repeat(3, 1fr);
    gap: 14px;
    margin: 10px 0 8px;
    perspective: 900px;
}
@media (max-width: 720px) {
    .cfy-reso-grid { grid-template-columns: 1fr; }
}
.cfy-reso-card {
    position: relative;
    border-radius: 18px;
    padding: 16px 15px 14px;
    background:
        linear-gradient(145deg, rgba(255,255,255,0.95), rgba(255,240,247,0.8));
    border: 1px solid rgba(244,114,182,0.3);
    box-shadow: 0 8px 24px rgba(244,114,182,0.12);
    overflow: hidden;
    transition: transform 0.4s cubic-bezier(.16,1,.3,1), box-shadow 0.4s ease;
    transform-style: preserve-3d;
}
@keyframes cfy-card-float {
    0%,100% { transform: translateY(0); }
    50% { transform: translateY(-5px); }
}
.cfy-reso-card:hover {
    transform: translateY(-8px) rotateX(4deg) rotateY(-3deg) scale(1.03);
    box-shadow:
        0 20px 45px rgba(236,72,153,0.28),
        0 0 0 1px rgba(244,114,182,0.4),
        0 0 40px rgba(244,114,182,0.2);
    animation: none;
}
.cfy-reso-card::before {
    content: '';
    position: absolute;
    inset: -1px;
    border-radius: 18px;
    padding: 1.5px;
    background: conic-gradient(
        from var(--cfy-angle, 0deg),
        transparent 0%, transparent 3%,
        rgba(255,255,255,0.95) 4%, rgba(244,114,182,1) 4.5%, rgba(232,121,249,0.8) 5%,
        transparent 5.5%, transparent 33%,
        rgba(255,255,255,0.95) 34%, rgba(244,114,182,1) 34.5%, rgba(232,121,249,0.8) 35%,
        transparent 35.5%, transparent 66%,
        rgba(255,255,255,0.95) 67%, rgba(244,114,182,1) 67.5%, rgba(232,121,249,0.8) 68%,
        transparent 68.5%, transparent 100%
    );
    -webkit-mask: linear-gradient(#fff 0 0) content-box, linear-gradient(#fff 0 0);
    mask: linear-gradient(#fff 0 0) content-box, linear-gradient(#fff 0 0);
    -webkit-mask-composite: xor;
    mask-composite: exclude;
    opacity: 0.7;
    pointer-events: none;
    z-index: 0;
}
.cfy-reso-card::after {
    content: '';
    position: absolute;
    top: -40%; right: -30%;
    width: 100px; height: 100px;
    border-radius: 50%;
    background: radial-gradient(circle, rgba(244,114,182,0.3), transparent 70%);
    pointer-events: none;
    transition: transform 0.4s ease;
}
.cfy-reso-card:hover::after {
    transform: scale(1.6) translate(-10px, 10px);
}
.cfy-reso-shine {
    position: absolute;
    inset: 0;
    background: linear-gradient(
        115deg,
        transparent 30%,
        rgba(255,255,255,0.55) 48%,
        transparent 62%
    );
    transform: translateX(-120%);
    pointer-events: none;
}
.cfy-reso-card:hover .cfy-reso-shine {
    animation: cfy-card-shine 0.8s ease forwards;
}
@keyframes cfy-card-shine {
    to { transform: translateX(120%); }
}
.cfy-reso-tag {
    display: inline-block;
    font-size: 10px;
    font-weight: 900;
    letter-spacing: 0.12em;
    text-transform: uppercase;
    color: #fff;
    background: linear-gradient(135deg, #f472b6, #ec4899, #e879f9);
    background-size: 200% 200%;
    border-radius: 999px;
    padding: 3px 10px;
    margin-bottom: 10px;
    box-shadow: 0 3px 12px rgba(236,72,153,0.4);
    position: relative;
    z-index: 1;
}
.cfy-reso-size {
    font-size: 18px;
    font-weight: 900;
    color: #9d174d;
    margin-bottom: 3px;
    position: relative;
    z-index: 1;
    letter-spacing: -0.02em;
}
.cfy-reso-name {
    font-size: 13px;
    color: #b06a8c;
    font-weight: 700;
    position: relative;
    z-index: 1;
}
.cfy-reso-hint {
    font-size: 11px;
    color: #c084a0;
    margin-top: 8px;
    line-height: 1.45;
    position: relative;
    z-index: 1;
}

/* 双栏 */
.cfy-grid-2 {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 0 20px;
}
@media (max-width: 720px) {
    .cfy-grid-2 { grid-template-columns: 1fr; }
}

/* 玻璃面板 */
.cfy-panel {
    position: relative;
    background: linear-gradient(150deg, rgba(255,255,255,0.78), rgba(255,240,247,0.58));
    border: 1px solid rgba(244,114,182,0.25);
    border-radius: 18px;
    padding: 16px 18px 18px;
    margin-top: 6px;
    backdrop-filter: blur(12px);
    box-shadow:
        0 10px 30px rgba(244,114,182,0.1),
        inset 0 1px 0 rgba(255,255,255,0.9);
    overflow: hidden;
    transition: box-shadow 0.35s ease, transform 0.35s cubic-bezier(.16,1,.3,1);
}
.cfy-panel:hover {
    box-shadow:
        0 16px 40px rgba(236,72,153,0.16),
        inset 0 1px 0 rgba(255,255,255,0.95);
}
.cfy-panel::before {
    content: '';
    position: absolute;
    top: 0; left: -40%;
    width: 40%; height: 100%;
    background: linear-gradient(90deg, transparent, rgba(255,255,255,0.35), transparent);
    pointer-events: none;
}
@keyframes cfy-panel-sweep {
    0%, 70% { left: -40%; }
    100% { left: 140%; }
}

/* 按钮 — 液态霓虹 */
.cfy-btn {
    position: relative;
    padding: 11px 24px;
    border-radius: 999px;
    border: none;
    background: linear-gradient(135deg, #f9a8d4 0%, #f472b6 30%, #ec4899 60%, #e879f9 100%);
    background-size: 220% 220%;
    color: #fff;
    cursor: pointer;
    font-size: 13px;
    font-weight: 900;
    font-family: inherit;
    margin: 8px 10px 8px 0;
    letter-spacing: 0.04em;
    box-shadow:
        0 8px 24px rgba(236,72,153,0.5),
        0 0 0 1px rgba(255,255,255,0.35) inset,
        0 0 30px rgba(244,114,182,0.3);
    transition: all 0.35s cubic-bezier(.16,1,.3,1);
    overflow: hidden;
    text-shadow: 0 1px 2px rgba(157,23,77,0.3);
}
.cfy-btn::before {
    content: '';
    position: absolute;
    top: 0; left: -80%;
    width: 50%; height: 100%;
    background: linear-gradient(90deg, transparent, rgba(255,255,255,0.55), transparent);
    transition: left 0.55s ease;
}
.cfy-btn::after {
    content: '';
    position: absolute;
    inset: -2px;
    border-radius: 999px;
    background: linear-gradient(135deg, #f472b6, #e879f9, #f472b6);
    z-index: -1;
    opacity: 0;
    filter: blur(10px);
    transition: opacity 0.35s ease;
}
.cfy-btn:hover {
    transform: translateY(-3px) scale(1.05);
    box-shadow:
        0 14px 36px rgba(236,72,153,0.65),
        0 0 0 1px rgba(255,255,255,0.5) inset,
        0 0 50px rgba(244,114,182,0.5);
}
.cfy-btn:hover::before { left: 140%; }
.cfy-btn:hover::after { opacity: 0.85; }
.cfy-btn:active { transform: translateY(0) scale(0.97); }

/* 识别结果 */
.cfy-detect {
    position: relative;
    background: linear-gradient(135deg, rgba(255,255,255,0.95), rgba(252,231,243,0.9));
    border: 1px solid rgba(236,72,153,0.35);
    border-left: 4px solid #ec4899;
    border-radius: 14px;
    padding: 12px 16px;
    margin: 12px 0 8px;
    font-size: 12.5px;
    color: #9d174d;
    white-space: pre-line;
    line-height: 1.8;
    box-shadow: 0 6px 20px rgba(236,72,153,0.15);
    animation: cfy-detect-in 0.5s cubic-bezier(.16,1,.3,1) both;
    overflow: hidden;
}
.cfy-detect::after {
    content: '';
    position: absolute;
    top: 0; left: 0; right: 0;
    height: 2px;
    background: linear-gradient(90deg, transparent, #f472b6, #e879f9, transparent);
}
@keyframes cfy-detect-in {
    from { opacity: 0; transform: translateY(8px); }
    to { opacity: 1; transform: translateY(0); }
}
@keyframes cfy-detect-line {
    0% { transform: translateX(-100%); }
    100% { transform: translateX(100%); }
}

/* 页脚 */
.cfy-footer {
    text-align: center;
    font-size: 11.5px;
    color: #c084a0;
    margin-top: 28px;
    padding-top: 16px;
    border-top: 1px solid rgba(244,114,182,0.2);
    letter-spacing: 0.1em;
    font-weight: 600;
    position: relative;
}
.cfy-footer::before {
    content: '✦  ✧  ✦';
    display: block;
    margin-bottom: 8px;
    font-size: 10px;
    letter-spacing: 0.4em;
    color: #f9a8d4;
}
.cfy-footer span {
    background: linear-gradient(90deg, #f472b6, #ec4899, #e879f9, #f472b6);
    background-size: 200% auto;
    -webkit-background-clip: text;
    background-clip: text;
    -webkit-text-fill-color: transparent;
    font-weight: 900;
}

/* 装饰彩条 */
.cfy-rainbow-bar {
    height: 3px;
    border-radius: 3px;
    margin: 4px 0 18px;
    background: linear-gradient(90deg,
        #f472b6, #ec4899, #e879f9, #c084fc, #f472b6, #fb7185, #f472b6);
    background-size: 300% 100%;
    box-shadow: 0 0 12px rgba(244,114,182,0.5);
}

/* ========== 分辨率卡片可编辑输入 ========== */
.cfy-reso-input-row {
    display: flex;
    align-items: center;
    gap: 6px;
    margin-bottom: 3px;
    position: relative;
    z-index: 1;
}
.cfy-reso-input {
    width: 64px;
    padding: 4px 6px;
    border: 1.5px solid rgba(244,114,182,0.35);
    border-radius: 8px;
    background: rgba(255,255,255,0.85);
    color: #9d174d;
    font-size: 13px;
    font-weight: 700;
    text-align: center;
    font-family: inherit;
    outline: none;
    transition: all 0.3s cubic-bezier(.16,1,.3,1);
    box-sizing: border-box;
}
.cfy-reso-input:hover {
    border-color: #f9a8d4;
    background: rgba(255,255,255,0.96);
}
.cfy-reso-input:focus {
    border-color: #ec4899;
    box-shadow: 0 0 0 3px rgba(236,72,153,0.18);
    background: #fff;
}
.cfy-reso-sep {
    font-weight: 900;
    color: #c084a0;
    font-size: 13px;
}

/* ========== 工作流扫描 ========== */
.cfy-scan-row {
    display: flex;
    gap: 8px;
    align-items: flex-end;
}
.cfy-scan-row > div:first-child {
    flex: 1;
}
.cfy-scan-btn {
    padding: 6px 16px;
    border-radius: 999px;
    border: 1.5px solid rgba(244,114,182,0.4);
    background: rgba(255,255,255,0.85);
    color: #be185d;
    cursor: pointer;
    font-size: 12px;
    font-weight: 800;
    font-family: inherit;
    white-space: nowrap;
    transition: all 0.3s cubic-bezier(.16,1,.3,1);
    backdrop-filter: blur(6px);
    letter-spacing: 0.03em;
}
.cfy-scan-btn:hover {
    background: linear-gradient(135deg, #f9a8d4, #f472b6);
    color: #fff;
    border-color: transparent;
    box-shadow: 0 4px 16px rgba(236,72,153,0.4);
}
.cfy-workflow-dropdown {
    margin-top: 6px;
}
.cfy-workflow-dropdown select {
    width: 100%;
    padding: 8px 12px;
    border: 1.5px solid rgba(244,114,182,0.3);
    border-radius: 12px;
    background: rgba(255,255,255,0.85);
    color: var(--ink);
    font-size: 12.5px;
    font-family: inherit;
    cursor: pointer;
    outline: none;
    transition: all 0.3s cubic-bezier(.16,1,.3,1);
    backdrop-filter: blur(6px);
    box-shadow: 0 2px 10px rgba(244,114,182,0.07);
}
.cfy-workflow-dropdown select:hover {
    border-color: #f9a8d4;
}
.cfy-workflow-dropdown select:focus {
    border-color: #ec4899;
    box-shadow: 0 0 0 3px rgba(236,72,153,0.18);
}

/* ========== 高级开关 ========== */
.cfy-advanced-toggle {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 12px 18px;
    margin: 8px 0;
    background: linear-gradient(135deg, rgba(255,255,255,0.85), rgba(252,231,243,0.8));
    border: 1px solid rgba(244,114,182,0.25);
    border-radius: 14px;
    cursor: pointer;
    transition: all 0.3s cubic-bezier(.16,1,.3,1);
    user-select: none;
}
.cfy-advanced-toggle:hover {
    border-color: #f9a8d4;
    box-shadow: 0 4px 16px rgba(244,114,182,0.15);
    transform: translateY(-1px);
}
.cfy-advanced-label {
    font-weight: 800;
    font-size: 13px;
    color: #9d174d;
    display: flex;
    align-items: center;
    gap: 8px;
}
.cfy-advanced-badge {
    display: inline-block;
    font-size: 10px;
    font-weight: 900;
    letter-spacing: 0.08em;
    color: #fff;
    background: linear-gradient(135deg, #f472b6, #ec4899);
    border-radius: 999px;
    padding: 2px 8px;
}
.cfy-advanced-switch {
    position: relative;
    width: 44px;
    height: 24px;
    background: rgba(244,114,182,0.3);
    border-radius: 12px;
    transition: background 0.3s ease;
    flex-shrink: 0;
}
.cfy-advanced-switch.active {
    background: linear-gradient(135deg, #f472b6, #ec4899);
}
.cfy-advanced-switch::after {
    content: '';
    position: absolute;
    top: 2px;
    left: 2px;
    width: 20px;
    height: 20px;
    background: #fff;
    border-radius: 50%;
    transition: transform 0.3s cubic-bezier(.16,1,.3,1);
    box-shadow: 0 1px 3px rgba(0,0,0,0.15);
}
.cfy-advanced-switch.active::after {
    transform: translateX(20px);
}

/* ========== 节点概览表 ========== */
.cfy-node-overview {
    margin: 10px 0 8px;
    border: 1px solid rgba(244,114,182,0.25);
    border-radius: 14px;
    overflow: hidden;
    background: rgba(255,255,255,0.85);
    backdrop-filter: blur(8px);
    animation: cfy-rise 0.6s cubic-bezier(.16,1,.3,1) both;
}
.cfy-node-table {
    width: 100%;
    border-collapse: collapse;
    font-size: 12px;
}
.cfy-node-table th {
    text-align: left;
    padding: 9px 14px;
    background: linear-gradient(135deg, rgba(252,231,243,0.95), rgba(255,240,247,0.8));
    color: #be185d;
    font-weight: 800;
    font-size: 11px;
    letter-spacing: 0.04em;
    text-transform: uppercase;
    border-bottom: 2px solid rgba(244,114,182,0.25);
}
.cfy-node-table td {
    padding: 8px 14px;
    border-bottom: 1px solid rgba(244,114,182,0.1);
    color: var(--ink);
    line-height: 1.5;
}
.cfy-node-table tr:hover td {
    background: rgba(252,231,243,0.5);
}
.cfy-node-table tr:last-child td {
    border-bottom: none;
}
.cfy-node-table .nid {
    font-family: ui-monospace, SFMono-Regular, monospace;
    font-size: 11px;
    color: #ec4899;
    font-weight: 700;
}
.cfy-node-table .params {
    color: #8b3a62;
    font-size: 11px;
    max-width: 200px;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
}
.cfy-node-count {
    font-size: 11px;
    color: #b06a8c;
    margin-bottom: 4px;
    font-weight: 600;
}

/* ========== 命名工作流卡片 ========== */
.cfy-wf-list {
    display: flex;
    flex-direction: column;
    gap: 10px;
    margin: 8px 0;
}
.cfy-wf-card {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 10px;
    padding: 10px 14px;
    border-radius: 14px;
    background: linear-gradient(135deg, rgba(255,255,255,0.9), rgba(255,240,247,0.7));
    border: 1px solid rgba(244,114,182,0.25);
    backdrop-filter: blur(10px);
    box-shadow: 0 4px 16px rgba(244,114,182,0.08);
    transition: all 0.3s cubic-bezier(.16,1,.3,1);
    position: relative;
    overflow: hidden;
}
.cfy-wf-card:hover {
    border-color: #f9a8d4;
    box-shadow: 0 8px 24px rgba(236,72,153,0.14);
    transform: translateY(-1px);
}
.cfy-wf-card::before {
    content: '';
    position: absolute;
    left: 0; top: 0; bottom: 0;
    width: 4px;
    border-radius: 4px 0 0 4px;
    background: linear-gradient(180deg, #f472b6, #ec4899, #e879f9);
    opacity: 0;
    transition: opacity 0.3s ease;
}
.cfy-wf-card.enabled::before {
    opacity: 1;
}
.cfy-wf-card.disabled {
    opacity: 0.55;
    background: linear-gradient(135deg, rgba(255,255,255,0.6), rgba(245,235,240,0.5));
}
.cfy-wf-toggle {
    width: 38px;
    height: 20px;
    border-radius: 10px;
    border: none;
    cursor: pointer;
    transition: all 0.3s ease;
    flex-shrink: 0;
    position: relative;
    outline: none;
}
.cfy-wf-toggle.on {
    background: linear-gradient(135deg, #f472b6, #ec4899);
    box-shadow: 0 0 10px rgba(236,72,153,0.4);
}
.cfy-wf-toggle.off {
    background: rgba(244,114,182,0.3);
}
.cfy-wf-toggle::after {
    content: '';
    position: absolute;
    top: 2px;
    width: 16px; height: 16px;
    border-radius: 50%;
    background: #fff;
    box-shadow: 0 1px 3px rgba(0,0,0,0.15);
    transition: transform 0.3s cubic-bezier(.16,1,.3,1);
}
.cfy-wf-toggle.on::after { left: 20px; }
.cfy-wf-toggle.off::after { left: 2px; }
.cfy-wf-name {
    width: 80px;
    flex-shrink: 0;
    border: 1.5px solid rgba(244,114,182,0.25);
    border-radius: 10px;
    padding: 6px 10px;
    font-size: 12.5px;
    font-weight: 700;
    color: #9d174d;
    background: rgba(255,255,255,0.8);
    font-family: inherit;
    outline: none;
    transition: all 0.3s ease;
    box-sizing: border-box;
}
.cfy-wf-name:hover, .cfy-wf-name:focus {
    border-color: #ec4899;
    box-shadow: 0 0 0 3px rgba(236,72,153,0.12);
}
.cfy-wf-path-row {
    flex: 1;
    display: flex;
    align-items: center;
    gap: 6px;
    min-width: 0;
}
.cfy-wf-path {
    flex: 1;
    border: 1.5px solid rgba(244,114,182,0.25);
    border-radius: 10px;
    padding: 6px 10px;
    font-size: 12px;
    color: #5b2145;
    background: rgba(255,255,255,0.8);
    font-family: ui-monospace, SFMono-Regular, monospace;
    outline: none;
    transition: all 0.3s ease;
    box-sizing: border-box;
    min-width: 0;
}
.cfy-wf-path:hover, .cfy-wf-path:focus {
    border-color: #ec4899;
    box-shadow: 0 0 0 3px rgba(236,72,153,0.12);
}
.cfy-wf-prefix-row {
    flex-basis: 100%;
    display: flex;
    align-items: center;
    gap: 6px;
    min-width: 0;
}
.cfy-wf-prefix-row::before {
    content: '前缀';
    flex-shrink: 0;
    font-size: 11px;
    font-weight: 700;
    color: #be185d;
    opacity: 0.85;
}
.cfy-wf-prefix {
    flex: 1;
    border: 1.5px dashed rgba(244,114,182,0.3);
    border-radius: 10px;
    padding: 6px 10px;
    font-size: 12px;
    color: #5b2145;
    background: rgba(255,255,255,0.7);
    font-family: inherit;
    outline: none;
    transition: all 0.3s ease;
    box-sizing: border-box;
    min-width: 0;
}
.cfy-wf-prefix:hover, .cfy-wf-prefix:focus {
    border-color: #ec4899;
    box-shadow: 0 0 0 3px rgba(236,72,153,0.12);
}
.cfy-wf-browse {
    padding: 6px 12px;
    border-radius: 10px;
    border: 1.5px solid rgba(244,114,182,0.35);
    background: rgba(255,255,255,0.85);
    color: #be185d;
    cursor: pointer;
    font-size: 11px;
    font-weight: 700;
    font-family: inherit;
    white-space: nowrap;
    transition: all 0.3s ease;
    flex-shrink: 0;
}
.cfy-wf-browse:hover {
    background: linear-gradient(135deg, #f9a8d4, #f472b6);
    color: #fff;
    border-color: transparent;
    box-shadow: 0 3px 12px rgba(236,72,153,0.35);
}
.cfy-wf-delete {
    width: 28px; height: 28px;
    border-radius: 50%;
    border: 1.5px solid rgba(244,114,182,0.3);
    background: rgba(255,255,255,0.8);
    color: #d6608a;
    cursor: pointer;
    font-size: 14px;
    display: flex;
    align-items: center;
    justify-content: center;
    font-family: inherit;
    transition: all 0.3s ease;
    flex-shrink: 0;
    padding: 0;
    line-height: 1;
}
.cfy-wf-delete:hover {
    background: #f43f5e;
    color: #fff;
    border-color: #f43f5e;
    box-shadow: 0 3px 12px rgba(244,63,94,0.35);
}
.cfy-wf-add {
    padding: 10px 20px;
    border-radius: 14px;
    border: 2px dashed rgba(244,114,182,0.35);
    background: rgba(255,255,255,0.7);
    color: #be185d;
    cursor: pointer;
    font-size: 12.5px;
    font-weight: 800;
    font-family: inherit;
    width: 100%;
    transition: all 0.3s ease;
    letter-spacing: 0.02em;
}
.cfy-wf-add:hover {
    border-color: #f472b6;
    background: rgba(252,231,243,0.85);
    box-shadow: 0 4px 16px rgba(244,114,182,0.18);
    transform: translateY(-1px);
}
.cfy-wf-browse-dropdown {
    position: absolute;
    top: 100%;
    left: 0; right: 0;
    z-index: 10;
    margin-top: 4px;
    border: 1px solid rgba(244,114,182,0.3);
    border-radius: 12px;
    background: rgba(255,255,255,0.97);
    box-shadow: 0 12px 32px rgba(236,72,153,0.2);
    max-height: 180px;
    overflow-y: auto;
    backdrop-filter: blur(12px);
}
.cfy-wf-browse-item {
    padding: 8px 14px;
    cursor: pointer;
    font-size: 12px;
    color: #5b2145;
    transition: all 0.15s ease;
    font-family: ui-monospace, SFMono-Regular, monospace;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
}
.cfy-wf-browse-item:hover {
    background: linear-gradient(90deg, rgba(244,114,182,0.15), rgba(236,72,153,0.08));
    color: #be185d;
}

/* ========== 提示词预设卡片 ========== */
.cfy-ps-list {
    display: flex;
    flex-direction: column;
    gap: 10px;
    margin: 8px 0;
}
.cfy-ps-card {
    border-radius: 14px;
    background: linear-gradient(135deg, rgba(255,255,255,0.9), rgba(255,240,247,0.7));
    border: 1px solid rgba(244,114,182,0.25);
    backdrop-filter: blur(10px);
    box-shadow: 0 4px 16px rgba(244,114,182,0.08);
    transition: all 0.3s cubic-bezier(.16,1,.3,1);
    overflow: hidden;
    position: relative;
}
.cfy-ps-card:hover {
    border-color: #f9a8d4;
    box-shadow: 0 8px 24px rgba(236,72,153,0.14);
}
.cfy-ps-card::before {
    content: '';
    position: absolute;
    left: 0; top: 0; bottom: 0;
    width: 4px;
    border-radius: 4px 0 0 4px;
    background: linear-gradient(180deg, #f472b6, #ec4899, #e879f9);
    opacity: 1;
}
.cfy-ps-head {
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 10px 14px 10px 18px;
    cursor: pointer;
}
.cfy-ps-name {
    flex: 1;
    font-size: 13px;
    font-weight: 700;
    color: #9d174d;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
}
.cfy-ps-preview {
    font-size: 11px;
    color: #a85569;
    opacity: 0.7;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    max-width: 200px;
    font-family: ui-monospace, SFMono-Regular, monospace;
}
.cfy-ps-expand {
    color: #d6608a;
    font-size: 12px;
    flex-shrink: 0;
    transition: transform 0.3s ease;
}
.cfy-ps-card.expanded .cfy-ps-expand { transform: rotate(90deg); }
.cfy-ps-body {
    padding: 0 14px 12px 18px;
    display: none;
}
.cfy-ps-card.expanded .cfy-ps-body { display: block; }
.cfy-ps-textarea {
    width: 100%;
    min-height: 80px;
    border: 1.5px solid rgba(244,114,182,0.25);
    border-radius: 10px;
    padding: 8px 10px;
    font-size: 12px;
    color: #5b2145;
    background: rgba(255,255,255,0.85);
    font-family: ui-monospace, SFMono-Regular, monospace;
    outline: none;
    transition: all 0.3s ease;
    box-sizing: border-box;
    resize: vertical;
}
.cfy-ps-textarea:focus {
    border-color: #ec4899;
    box-shadow: 0 0 0 3px rgba(236,72,153,0.12);
}
.cfy-ps-actions {
    display: flex;
    gap: 8px;
    margin-top: 8px;
}
.cfy-ps-btn {
    padding: 6px 14px;
    border-radius: 10px;
    border: 1.5px solid rgba(244,114,182,0.35);
    background: rgba(255,255,255,0.85);
    color: #be185d;
    cursor: pointer;
    font-size: 11px;
    font-weight: 700;
    font-family: inherit;
    white-space: nowrap;
    transition: all 0.3s ease;
}
.cfy-ps-btn:hover {
    background: linear-gradient(135deg, #f9a8d4, #f472b6);
    color: #fff;
    border-color: transparent;
    box-shadow: 0 3px 12px rgba(236,72,153,0.35);
}
.cfy-ps-btn.danger {
    border-color: rgba(220,38,38,0.3);
    color: #b91c1c;
}
.cfy-ps-btn.danger:hover {
    background: linear-gradient(135deg, #f87171, #ef4444);
    color: #fff;
    border-color: transparent;
}
.cfy-ps-add-row {
    display: flex;
    gap: 8px;
    margin-bottom: 10px;
    flex-wrap: wrap;
}
.cfy-ps-add-name {
    width: 120px;
    border: 1.5px solid rgba(244,114,182,0.25);
    border-radius: 10px;
    padding: 6px 10px;
    font-size: 12.5px;
    font-weight: 700;
    color: #9d174d;
    background: rgba(255,255,255,0.8);
    font-family: inherit;
    outline: none;
    transition: all 0.3s ease;
    box-sizing: border-box;
}
.cfy-ps-add-name:focus { border-color: #ec4899; box-shadow: 0 0 0 3px rgba(236,72,153,0.12); }
.cfy-ps-add-content {
    flex: 1;
    min-width: 200px;
    min-height: 40px;
    border: 1.5px solid rgba(244,114,182,0.25);
    border-radius: 10px;
    padding: 6px 10px;
    font-size: 12px;
    color: #5b2145;
    background: rgba(255,255,255,0.8);
    font-family: ui-monospace, SFMono-Regular, monospace;
    outline: none;
    transition: all 0.3s ease;
    box-sizing: border-box;
    resize: vertical;
}
.cfy-ps-add-content:focus { border-color: #ec4899; box-shadow: 0 0 0 3px rgba(236,72,153,0.12); }
.cfy-ps-add-btn {
    padding: 6px 16px;
    border-radius: 10px;
    border: none;
    background: linear-gradient(135deg, #f472b6, #ec4899);
    color: #fff;
    cursor: pointer;
    font-size: 12px;
    font-weight: 700;
    font-family: inherit;
    white-space: nowrap;
    transition: all 0.3s ease;
    flex-shrink: 0;
    box-shadow: 0 3px 12px rgba(236,72,153,0.3);
}
.cfy-ps-add-btn:hover {
    background: linear-gradient(135deg, #ec4899, #db2777);
    box-shadow: 0 6px 20px rgba(236,72,153,0.45);
    transform: translateY(-1px);
}
.cfy-ps-empty {
    text-align: center;
    padding: 16px;
    color: #a85569;
    font-size: 12px;
    opacity: 0.7;
}
";

    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        if (Configuration == null)
        {
            b.AddContent(0, "Configuration NULL");
            return;
        }

        int i = 0;

        b.OpenElement(i++, "style");
        b.AddContent(i++, Css);
        b.CloseElement();

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-root");

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-container");

        // 装饰层
        b.OpenElement(i++, "div"); b.AddAttribute(i++, "class", "cfy-aurora"); b.CloseElement();
        b.OpenElement(i++, "div"); b.AddAttribute(i++, "class", "cfy-grid-bg"); b.CloseElement();
        b.OpenElement(i++, "div"); b.AddAttribute(i++, "class", "cfy-orb cfy-orb-1"); b.CloseElement();
        b.OpenElement(i++, "div"); b.AddAttribute(i++, "class", "cfy-orb cfy-orb-2"); b.CloseElement();
        b.OpenElement(i++, "div"); b.AddAttribute(i++, "class", "cfy-orb cfy-orb-3"); b.CloseElement();
        b.OpenElement(i++, "div"); b.AddAttribute(i++, "class", "cfy-orb cfy-orb-4"); b.CloseElement();

        // 粒子
        for (int p = 1; p <= 12; p++)
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", $"cfy-particle cfy-p{p}");
            b.CloseElement();
        }
        // 闪星
        for (int s = 1; s <= 5; s++)
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", $"cfy-star cfy-star-{s}");
            b.CloseElement();
        }

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-content cfy-stagger");

        // Hero
        var configured = !string.IsNullOrWhiteSpace(Configuration.BaseUrl)
                         && !string.IsNullOrWhiteSpace(Configuration.WorkflowPath);

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-hero");

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-title-wrap");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-kicker");
        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "class", "cfy-kicker-dot");
        b.CloseElement();
        b.AddContent(i++, "Doro · ComfyUI");
        b.CloseElement();
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-title");
        b.AddContent(i++, "ComfyUI 生图");
        b.CloseElement();
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-subtitle");
        b.AddContent(i++, "任意工作流 · 智能分辨率 · 固定提示词前缀");
        b.CloseElement();
        b.CloseElement();

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-badge-wrap");
        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "class", configured ? "cfy-badge-on" : "cfy-badge-off");
        b.AddContent(i++, configured ? "已配置 · LIVE" : "未配置");
        b.CloseElement();
        b.CloseElement();

        b.CloseElement(); // hero

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-rainbow-bar");
        b.CloseElement();

        // 使用说明
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-alert");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-alert-title");
        b.AddContent(i++, "功能说明");
        b.CloseElement();
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-alert-desc");
        b.AddContent(i++,
            "【角色提示词检索】内置数千个动漫/游戏角色的中英文索引，AI 生图前可自动检索角色触发词、稳定外貌和默认服装，大幅提升角色还原度。原创角色和普通人物不检索。\n" +
            "【提示词预设】可在下方保存常用提示词片段（角色人设/动作/背景），AI 生图时按需调用复用。支持实时增删改；也可在聊天中直接发预设内容给 AI，让 AI 自主调用函数存储。\n" +
            "【在线标签检索】可选：自然语言→标准 Danbooru 标签（默认关）。有画面细节时 AI 可检索以提升质量；三种提示词模式均适用。大陆建议保留默认主源备份域。\n" +
            "【使用步骤】1. 启动 ComfyUI → 2. 配置地址和工作流 → 3. AI 调用 GenerateImage 生图\n" +
            "【分辨率】portrait 竖版 / landscape 横版 / square 正方形，AI 智能选择或手动指定");
        b.CloseElement();
        b.CloseElement();

        // 分辨率
        AddSection(b, ref i, "分辨率预设");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-reso-grid");
        
        // Portrait card
        AddEditableResoCard(b, ref i, "PORTRAIT", "竖版", "全身立绘 · 人物 · 手机壁纸",
            Configuration.PortraitWidth, v => Configuration.PortraitWidth = v,
            Configuration.PortraitHeight, v => Configuration.PortraitHeight = v);
        // Landscape card
        AddEditableResoCard(b, ref i, "LANDSCAPE", "横版", "风景 · 场景 · 横构图",
            Configuration.LandscapeWidth, v => Configuration.LandscapeWidth = v,
            Configuration.LandscapeHeight, v => Configuration.LandscapeHeight = v);
        // Square card
        AddEditableResoCard(b, ref i, "SQUARE", "正方形", "头像 · 图标 · 对称构图",
            Configuration.SquareWidth, v => Configuration.SquareWidth = v,
            Configuration.SquareHeight, v => Configuration.SquareHeight = v);
        
        b.CloseElement();
        AddHint(b, ref i, "AI 传 orientation=portrait/landscape/square 智能选档；也可直接指定 width/height 覆盖。点击数值即可修改预设分辨率。");

        // 连接 + 工作流
        AddSection(b, ref i, "连接与工作流");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-panel");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-grid-2");

        // 左栏
        b.OpenElement(i++, "div");
        AddInput(b, ref i, "ComfyUI 地址", Configuration.BaseUrl, v => Configuration.BaseUrl = v);
        AddHint(b, ref i, "例如 http://127.0.0.1:8188");
        AddInput(b, ref i, "API Token（可选）", Configuration.ApiToken, v => Configuration.ApiToken = v);

        // 额外保存副本开关
        b.OpenElement(i++, "label");
        b.AddAttribute(i++, "style", "display:inline-flex;align-items:center;gap:8px;cursor:pointer;margin-top:4px;");
        b.OpenElement(i++, "input");
        b.AddAttribute(i++, "type", "checkbox");
        b.AddAttribute(i++, "checked", Configuration.ExtraSaveCopy);
        b.AddAttribute(i++, "style", "accent-color:#ec4899;width:16px;height:16px;cursor:pointer;flex-shrink:0;");
        b.AddAttribute(i++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            Configuration.ExtraSaveCopy = (bool)(e.Value ?? false);
            StateHasChanged();
        }));
        b.CloseElement();
        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "style", "font-size:12.5px;color:#9d174d;font-weight:700;white-space:nowrap;");
        b.AddContent(i++, "额外保存副本：开启时复制到指定目录；关闭时直接用工作流保存路径");
        b.CloseElement();
        b.CloseElement();

        // 开启时才显示图片保存目录
        if (Configuration.ExtraSaveCopy)
        {
            AddInput(b, ref i, "图片保存目录", Configuration.SaveDirectory, v => Configuration.SaveDirectory = v);
            var currentSave = string.IsNullOrWhiteSpace(Configuration.SaveDirectory)
                ? Path.Combine(AlifePath.StorageFolderPath, "Images", "Comfyui")
                : Configuration.SaveDirectory;
            AddHint(b, ref i, $"当前: {currentSave}（留空用默认）");
        }
        else
        {
            AddHint(b, ref i, "已关闭：直接使用工作流内保存节点的路径，不再额外复制或下载");
        }

        // ComfyUI output 目录始终显示（独立于额外保存开关）
        AddInput(b, ref i, "ComfyUI output 目录（可选）", Configuration.ComfyuiOutputPath, v => Configuration.ComfyuiOutputPath = v);
        AddHint(b, ref i, "填写 ComfyUI 的 output 绝对路径后，标准 SaveImage 的图片直接引用该目录，不再重复下载");
        b.CloseElement();

        // 右栏 — 默认工作流 + 扫描
        b.OpenElement(i++, "div");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-label");
        b.AddContent(i++, "默认工作流");
        b.CloseElement();
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-scan-row");
        b.OpenElement(i++, "div");
        b.OpenComponent<Input<string>>(i++);
        b.AddAttribute(i++, "Value", Configuration.WorkflowPath ?? "");
        b.AddAttribute(i++, "ValueChanged", EventCallback.Factory.Create<string>(this, v => Configuration.WorkflowPath = v));
        b.AddAttribute(i++, "Style", "width:100%;");
        b.CloseComponent();
        b.CloseElement();
        b.OpenElement(i++, "button");
        b.AddAttribute(i++, "type", "button");
        b.AddAttribute(i++, "class", "cfy-scan-btn");
        b.AddAttribute(i++, "onclick", EventCallback.Factory.Create(this, ScanWorkflows));
        b.AddContent(i++, "扫描");
        b.CloseElement();
        b.CloseElement();
        AddHint(b, ref i, "填目录路径后点「扫描」浏览工作流文件，也可在下方多工作流卡片中手动选择");

        // 扫描结果下拉
        if (_showWorkflowDropdown && _scannedWorkflows.Count > 0)
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cfy-workflow-dropdown");
            b.OpenElement(i++, "select");
            b.AddAttribute(i++, "size", "6");
            b.AddAttribute(i++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
            {
                Configuration.WorkflowPath = e.Value?.ToString() ?? "";
            }));
            foreach (var wf in _scannedWorkflows)
            {
                b.OpenElement(i++, "option");
                b.AddAttribute(i++, "value", wf);
                b.AddAttribute(i++, "title", wf);
                var display = GetWorkflowDisplayName(wf, _scanDir);
                b.AddContent(i++, display);
                b.CloseElement();
            }
            b.CloseElement();
            b.CloseElement();
        }

        b.CloseElement(); // right
        b.CloseElement(); // grid
        b.CloseElement(); // panel

        // 显示扫描状态
        if (!string.IsNullOrWhiteSpace(_scanDetectMessage))
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cfy-detect");
            b.AddContent(i++, _scanDetectMessage);
            b.CloseElement();
        }

        // ========== 模型卸载与显存释放 ==========
        AddSection(b, ref i, "模型卸载与显存释放");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-panel");

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "style", "display:flex;align-items:center;gap:10px;margin-bottom:10px;flex-wrap:wrap;");
        b.OpenElement(i++, "button");
        b.AddAttribute(i++, "type", "button");
        b.AddAttribute(i++, "class", "cfy-btn");
        b.AddAttribute(i++, "disabled", _unloadingModel);
        b.AddAttribute(i++, "onclick", EventCallback.Factory.Create(this, UnloadModels));
        b.AddContent(i++, _unloadingModel ? "卸载中…" : "✦ 立即卸载模型（释放显存）");
        b.CloseElement();
        if (!string.IsNullOrWhiteSpace(_unloadMessage))
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cfy-detect");
            b.AddAttribute(i++, "style", "margin:0;flex:1;");
            b.AddContent(i++, _unloadMessage);
            b.CloseElement();
        }
        b.CloseElement();

        // 空闲自动卸载开关
        b.OpenElement(i++, "label");
        b.AddAttribute(i++, "style", "display:inline-flex;align-items:center;gap:8px;cursor:pointer;");
        b.OpenElement(i++, "input");
        b.AddAttribute(i++, "type", "checkbox");
        b.AddAttribute(i++, "checked", Configuration.EnableAutoUnload);
        b.AddAttribute(i++, "style", "accent-color:#ec4899;width:16px;height:16px;cursor:pointer;flex-shrink:0;");
        b.AddAttribute(i++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            Configuration.EnableAutoUnload = (bool)(e.Value ?? false);
            StateHasChanged();
        }));
        b.CloseElement();
        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "style", "font-size:12.5px;color:#9d174d;font-weight:700;white-space:nowrap;");
        b.AddContent(i++, "空闲自动卸载：距上次生图空闲超时自动卸载模型释放显存");
        b.CloseElement();
        b.CloseElement();

        if (Configuration.EnableAutoUnload)
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "style", "display:flex;align-items:center;gap:12px;margin-top:10px;flex-wrap:wrap;");
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "style", "width:160px;flex-shrink:0;");
            AddInput(b, ref i, "空闲小时数", Configuration.AutoUnloadIdleHours.ToString(), v =>
            {
                if (int.TryParse(v, out var n))
                    Configuration.AutoUnloadIdleHours = Math.Clamp(n, 0, 720);
            });
            b.CloseElement();
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "style", "width:160px;flex-shrink:0;");
            AddInput(b, ref i, "空闲分钟数", Configuration.AutoUnloadIdleMinutes.ToString(), v =>
            {
                if (int.TryParse(v, out var n))
                    Configuration.AutoUnloadIdleMinutes = Math.Clamp(n, 0, 59);
            });
            b.CloseElement();
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "style", "font-size:11px;color:#b06a8c;font-weight:600;");
            b.AddContent(i++, $"合计空闲 {Configuration.AutoUnloadIdleHours} 小时 {Configuration.AutoUnloadIdleMinutes} 分钟后自动卸载（需保存配置后生效）");
            b.CloseElement();
            b.CloseElement();
        }

        b.CloseElement(); // panel

        // ========== 多工作流（可选） ==========
        AddSection(b, ref i, "多工作流（可选）");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-panel");

        // 初始化卡片
        if (_workflowCards.Count == 0) LoadWorkflowCards();

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-wf-list");

        for (int ci = 0; ci < _workflowCards.Count; ci++)
        {
            var cardIndex = ci;
            var card = _workflowCards[ci];

            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", $"cfy-wf-card{(card.Enabled ? " enabled" : " disabled")}");

            // 启用开关
            b.OpenElement(i++, "button");
            b.AddAttribute(i++, "type", "button");
            b.AddAttribute(i++, "class", $"cfy-wf-toggle{(card.Enabled ? " on" : " off")}");
            b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e =>
            {
                ToggleWorkflowCardEnabled(cardIndex);
            }));
            b.CloseElement();

            // 名称输入
            b.OpenElement(i++, "input");
            b.AddAttribute(i++, "class", "cfy-wf-name");
            b.AddAttribute(i++, "value", card.Name);
            b.AddAttribute(i++, "placeholder", "名称");
            b.AddAttribute(i++, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
            {
                UpdateWorkflowCardName(cardIndex, e.Value?.ToString() ?? "");
            }));
            b.CloseElement();

            // 路径行
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cfy-wf-path-row");

            b.OpenElement(i++, "input");
            b.AddAttribute(i++, "class", "cfy-wf-path");
            b.AddAttribute(i++, "value", card.Path);
            b.AddAttribute(i++, "placeholder", "工作流 JSON 路径...");
            b.AddAttribute(i++, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
            {
                UpdateWorkflowCardPath(cardIndex, e.Value?.ToString() ?? "");
            }));
            b.CloseElement();

            // 浏览按钮
            b.OpenElement(i++, "button");
            b.AddAttribute(i++, "type", "button");
            b.AddAttribute(i++, "class", "cfy-wf-browse");
            b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e =>
            {
                ToggleWorkflowScan(cardIndex);
            }));
            b.AddContent(i++, "浏览");
            b.CloseElement();

            b.CloseElement(); // path-row

            // 固定正向前缀输入
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cfy-wf-prefix-row");
            b.OpenElement(i++, "input");
            b.AddAttribute(i++, "class", "cfy-wf-prefix");
            b.AddAttribute(i++, "value", card.Prefix);
            b.AddAttribute(i++, "placeholder", "固定正向前缀（tag/短句；留空沿用全局前缀）");
            b.AddAttribute(i++, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
            {
                UpdateWorkflowCardPrefix(cardIndex, e.Value?.ToString() ?? "");
            }));
            b.CloseElement();
            b.CloseElement(); // prefix-row

            // 删除按钮
            b.OpenElement(i++, "button");
            b.AddAttribute(i++, "type", "button");
            b.AddAttribute(i++, "class", "cfy-wf-delete");
            b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e =>
            {
                RemoveWorkflowCard(cardIndex);
            }));
            b.AddContent(i++, "✕");
            b.CloseElement();

            b.CloseElement(); // card

            // 浏览下拉
            if (_activeScanCardIndex == cardIndex && _scannedWorkflows.Count > 0)
            {
                b.OpenElement(i++, "div");
                b.AddAttribute(i++, "style", "position:relative;");
                b.OpenElement(i++, "div");
                b.AddAttribute(i++, "class", "cfy-wf-browse-dropdown");
                foreach (var wf in _scannedWorkflows)
                {
                    var wfPath = wf;
                    b.OpenElement(i++, "div");
                    b.AddAttribute(i++, "class", "cfy-wf-browse-item");
                    b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e =>
                    {
                        UpdateWorkflowCardPath(cardIndex, wfPath);
                        _activeScanCardIndex = -1;
                        StateHasChanged();
                    }));
                    b.AddContent(i++, Path.GetFileName(wfPath));
                    b.CloseElement();
                }
                b.CloseElement();
                b.CloseElement();
            }
        }

        b.CloseElement(); // wf-list

        // 添加按钮
        b.OpenElement(i++, "button");
        b.AddAttribute(i++, "type", "button");
        b.AddAttribute(i++, "class", "cfy-wf-add");
        b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e =>
        {
            AddWorkflowCard();
        }));
        b.AddContent(i++, "+ 添加工作流");
        b.CloseElement();

        b.CloseElement(); // panel

        // ========== 提示词预设 ==========
        AddSection(b, ref i, "提示词预设");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-panel");

        AddHint(b, ref i, "保存常用提示词片段（角色人设/复杂动作/完整背景），AI 生图时按需调用复用。数据存在 Storage/Config/Alife.Plugin.Comfyui/（插件更新不会清空）；也可聊天里让 AI 调用 savepromptpreset。");

        if (_presetCards.Count == 0) LoadPresetCards();

        // 新增预设输入区
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-ps-add-row");
        b.OpenElement(i++, "input");
        b.AddAttribute(i++, "class", "cfy-ps-add-name");
        b.AddAttribute(i++, "value", _newPresetName);
        b.AddAttribute(i++, "placeholder", "预设名称");
        b.AddAttribute(i++, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            _newPresetName = e.Value?.ToString() ?? "";
        }));
        b.CloseElement();
        b.OpenElement(i++, "textarea");
        b.AddAttribute(i++, "class", "cfy-ps-add-content");
        b.AddAttribute(i++, "value", _newPresetContent);
        b.AddAttribute(i++, "placeholder", "提示词内容（tag 串或自然语言）...");
        b.AddAttribute(i++, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            _newPresetContent = e.Value?.ToString() ?? "";
        }));
        b.CloseElement();
        b.OpenElement(i++, "button");
        b.AddAttribute(i++, "type", "button");
        b.AddAttribute(i++, "class", "cfy-ps-add-btn");
        b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e =>
        {
            AddPresetCard();
        }));
        b.AddContent(i++, "+ 保存预设");
        b.CloseElement();
        b.CloseElement(); // add-row

        // 预设卡片列表
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-ps-list");

        if (_presetCards.Count == 0)
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cfy-ps-empty");
            b.AddContent(i++, "暂无预设。上方输入名称和内容后点「保存预设」，或在聊天中发内容给 AI 让其自主存储");
            b.CloseElement();
        }

        for (int pi = 0; pi < _presetCards.Count; pi++)
        {
            var presetIndex = pi;
            var preset = _presetCards[pi];
            var expanded = _activePresetIndex == pi;

            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", $"cfy-ps-card{(expanded ? " expanded" : "")}");

            // 卡片头部（点击展开）
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cfy-ps-head");
            b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e =>
            {
                TogglePresetExpand(presetIndex);
            }));
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cfy-ps-name");
            b.AddContent(i++, preset.Name);
            b.CloseElement();
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cfy-ps-preview");
            var preview = preset.Content.Length > 50
                ? preset.Content[..50] + "..." : preset.Content;
            b.AddContent(i++, preview);
            b.CloseElement();
            b.OpenElement(i++, "span");
            b.AddAttribute(i++, "class", "cfy-ps-expand");
            b.AddContent(i++, "▶");
            b.CloseElement();
            b.CloseElement(); // head

            // 展开内容（编辑区）
            if (expanded)
            {
                b.OpenElement(i++, "div");
                b.AddAttribute(i++, "class", "cfy-ps-body");

                b.OpenElement(i++, "input");
                b.AddAttribute(i++, "class", "cfy-ps-add-name");
                b.AddAttribute(i++, "style", "width:100%;margin-bottom:8px;");
                b.AddAttribute(i++, "value", preset.Name);
                b.AddAttribute(i++, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
                {
                    UpdatePresetCardName(presetIndex, e.Value?.ToString() ?? "");
                }));
                b.CloseElement();

                b.OpenElement(i++, "textarea");
                b.AddAttribute(i++, "class", "cfy-ps-textarea");
                b.AddAttribute(i++, "value", preset.Content);
                b.AddAttribute(i++, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
                {
                    UpdatePresetCardContent(presetIndex, e.Value?.ToString() ?? "");
                }));
                b.CloseElement();

                b.OpenElement(i++, "div");
                b.AddAttribute(i++, "class", "cfy-ps-actions");
                b.OpenElement(i++, "button");
                b.AddAttribute(i++, "type", "button");
                b.AddAttribute(i++, "class", "cfy-ps-btn danger");
                b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e =>
                {
                    RemovePresetCard(presetIndex);
                }));
                b.AddContent(i++, "删除");
                b.CloseElement();
                b.CloseElement(); // actions

                b.CloseElement(); // body
            }

            b.CloseElement(); // card
        }

        b.CloseElement(); // ps-list
        b.CloseElement(); // panel

        // 提示词
        AddSection(b, ref i, "提示词前缀与负面");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-panel");
        AddTextArea(b, ref i, "固定正向提示词前缀（tag/短句，逗号或换行均可）", Configuration.PositivePromptPrefix, v => Configuration.PositivePromptPrefix = v, 5);
        AddHint(b, ref i, "与 AI 提示词合并后自动去重；最终注入统一为英文逗号+空格，如：masterpiece, best quality。命名工作流可在各自卡片里单独设前缀（留空则沿用这里）；默认工作流始终用这里。留空则不拼接");
        AddTextArea(b, ref i, "固定负面提示词（可空=用工作流自带）", Configuration.NegativePrompt, v => Configuration.NegativePrompt = v, 3);
        AddHint(b, ref i, "留空用工作流自带负面；填写则覆盖并规范为英文逗号分隔");
        AddSelect(b, ref i, "提示词种类", Configuration.PromptStyle, v => Configuration.PromptStyle = v, new[]
        {
            ("tag", "纯 Tag — 全小写英文标签，逗号分隔"),
            ("natural", "自然语言 — 角色 Tag 置前 + 英文短句"),
            ("hybrid", "混合模式 — 静态用标签，动作/关系用短句；多人每角色独立描述")
        });
        AddHint(b, ref i, "控制 AI 生成提示词的格式风格，不影响已有前缀");
        b.CloseElement();

        // 在线 Danbooru 语义标签检索
        AddSection(b, ref i, "在线标签检索（可选）");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-panel");

        b.OpenElement(i++, "label");
        b.AddAttribute(i++, "style", "display:inline-flex;align-items:center;gap:8px;cursor:pointer;margin-bottom:8px;");
        b.OpenElement(i++, "input");
        b.AddAttribute(i++, "type", "checkbox");
        b.AddAttribute(i++, "checked", Configuration.EnableDanbooruSearch);
        b.AddAttribute(i++, "style", "accent-color:#ec4899;width:16px;height:16px;cursor:pointer;flex-shrink:0;");
        b.AddAttribute(i++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            Configuration.EnableDanbooruSearch = (bool)(e.Value ?? false);
            if (!Configuration.EnableDanbooruSearch)
                Configuration.EnableDanbooruArtistRecommend = false;
            StateHasChanged();
        }));
        b.CloseElement();
        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "style", "font-size:12.5px;color:#9d174d;font-weight:700;");
        b.AddContent(i++, "启用 Danbooru 语义标签检索（search / related）");
        b.CloseElement();
        b.CloseElement();

        AddHint(b, ref i, "质量优先：有服装/姿势/场景等细节时 AI 可检索标准 tag。默认关=零外网。需重载模块/重启角色后函数才注册。");

        b.OpenElement(i++, "label");
        b.AddAttribute(i++, "style",
            $"display:inline-flex;align-items:center;gap:8px;cursor:{(Configuration.EnableDanbooruSearch ? "pointer" : "not-allowed")};margin:10px 0 8px;opacity:{(Configuration.EnableDanbooruSearch ? "1" : "0.45")};");
        b.OpenElement(i++, "input");
        b.AddAttribute(i++, "type", "checkbox");
        b.AddAttribute(i++, "checked", Configuration.EnableDanbooruArtistRecommend);
        b.AddAttribute(i++, "disabled", !Configuration.EnableDanbooruSearch);
        b.AddAttribute(i++, "style", "accent-color:#ec4899;width:16px;height:16px;cursor:pointer;flex-shrink:0;");
        b.AddAttribute(i++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            if (!Configuration.EnableDanbooruSearch) return;
            Configuration.EnableDanbooruArtistRecommend = (bool)(e.Value ?? false);
            StateHasChanged();
        }));
        b.CloseElement();
        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "style", "font-size:12.5px;color:#9d174d;font-weight:700;");
        b.AddContent(i++, "启用画师推荐（额外外网调用，默认关；依赖总开关）");
        b.CloseElement();
        b.CloseElement();

        if (Configuration.EnableDanbooruSearch)
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cfy-grid-2");
            b.AddAttribute(i++, "style", "margin-top:8px;");

            b.OpenElement(i++, "div");
            AddInput(b, ref i, "主源 URL", Configuration.DanbooruSearchPrimaryUrl,
                v => Configuration.DanbooruSearchPrimaryUrl = v);
            AddHint(b, ref i, "默认官方备份域（大陆通常更快）。自建时填你的地址，将不再自动回退 HF");
            AddInput(b, ref i, "超时秒数（总预算）", Configuration.DanbooruSearchTimeoutSeconds.ToString(), v =>
            {
                if (int.TryParse(v, out var n))
                    Configuration.DanbooruSearchTimeoutSeconds = Math.Clamp(n, 10, 120);
            });
            b.CloseElement();

            b.OpenElement(i++, "div");
            AddInput(b, ref i, "备用 URL（可空）", Configuration.DanbooruSearchFallbackUrl,
                v => Configuration.DanbooruSearchFallbackUrl = v);
            AddHint(b, ref i, "默认 HF Space；主源失败时回退。HF 可能冷启 30–60s，大陆常较慢");
            b.OpenElement(i++, "label");
            b.AddAttribute(i++, "style", "display:inline-flex;align-items:center;gap:8px;cursor:pointer;margin-top:12px;");
            b.OpenElement(i++, "input");
            b.AddAttribute(i++, "type", "checkbox");
            b.AddAttribute(i++, "checked", Configuration.DanbooruSearchShowNsfw);
            b.AddAttribute(i++, "style", "accent-color:#ec4899;width:16px;height:16px;cursor:pointer;flex-shrink:0;");
            b.AddAttribute(i++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
            {
                Configuration.DanbooruSearchShowNsfw = (bool)(e.Value ?? false);
            }));
            b.CloseElement();
            b.OpenElement(i++, "span");
            b.AddAttribute(i++, "style", "font-size:12.5px;color:#9d174d;font-weight:700;");
            b.AddContent(i++, "包含 NSFW 标签（默认关，用 SFW）");
            b.CloseElement();
            b.CloseElement();
            b.CloseElement();

            b.CloseElement(); // grid-2

            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "style", "display:flex;align-items:center;gap:10px;margin-top:12px;flex-wrap:wrap;");
            b.OpenElement(i++, "button");
            b.AddAttribute(i++, "type", "button");
            b.AddAttribute(i++, "class", "cfy-btn");
            b.AddAttribute(i++, "disabled", _danbooruTesting);
            b.AddAttribute(i++, "onclick", EventCallback.Factory.Create(this, TestDanbooruConnectivity));
            b.AddContent(i++, _danbooruTesting ? "测试中…" : "✦ 测试连通");
            b.CloseElement();
            if (!string.IsNullOrWhiteSpace(_danbooruTestMessage))
            {
                b.OpenElement(i++, "div");
                b.AddAttribute(i++, "class", "cfy-detect");
                b.AddAttribute(i++, "style", "margin:0;flex:1;");
                b.AddContent(i++, _danbooruTestMessage);
                b.CloseElement();
            }
            b.CloseElement();

            AddHint(b, ref i,
                "公开服务请友情链接上游：https://huggingface.co/spaces/SAkizuki/DanbooruSearch 。自建最稳；失败时 AI 会自写 tag 仍可生图。");
        }

        b.CloseElement(); // danbooru panel

        // 在线角色检索扩充（AnimaDex）：本地角色索引未命中/歧义时自动联网补查
        AddSection(b, ref i, "在线角色检索扩充（可选）");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-panel");

        b.OpenElement(i++, "label");
        b.AddAttribute(i++, "style", "display:inline-flex;align-items:center;gap:8px;cursor:pointer;margin-bottom:8px;");
        b.OpenElement(i++, "input");
        b.AddAttribute(i++, "type", "checkbox");
        b.AddAttribute(i++, "checked", Configuration.EnableAnimadexCharacterSearch);
        b.AddAttribute(i++, "style", "accent-color:#ec4899;width:16px;height:16px;cursor:pointer;flex-shrink:0;");
        b.AddAttribute(i++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            Configuration.EnableAnimadexCharacterSearch = (bool)(e.Value ?? false);
            StateHasChanged();
        }));
        b.CloseElement();
        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "style", "font-size:12.5px;color:#9d174d;font-weight:700;");
        b.AddContent(i++, "启用 AnimaDex 在线角色检索");
        b.CloseElement();
        b.CloseElement();

        AddHint(b, ref i, "仅作为本地角色索引（character-prompts.json）的补充：本地未命中、或带作品名仍歧义时，自动联网查约 3.6 万角色在线库。本地命中/歧义处理仍优先，不影响离线质量。默认关=零外网。需重载模块/重启角色后生效");

        if (Configuration.EnableAnimadexCharacterSearch)
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cfy-grid-2");
            b.AddAttribute(i++, "style", "margin-top:8px;");

            b.OpenElement(i++, "div");
            AddInput(b, ref i, "服务地址", Configuration.AnimadexBaseUrl,
                v => Configuration.AnimadexBaseUrl = v);
            AddHint(b, ref i, "默认官方站点 animadex.net（大陆直连通常约 1s）。中文角色名本地未收录时，返回会提示改用英文/罗马字名");
            AddInput(b, ref i, "超时秒数", Configuration.AnimadexTimeoutSeconds.ToString(), v =>
            {
                if (int.TryParse(v, out var n))
                    Configuration.AnimadexTimeoutSeconds = Math.Clamp(n, 5, 60);
            });
            b.CloseElement();

            b.OpenElement(i++, "div");
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "style", "display:flex;align-items:center;gap:10px;margin-top:12px;flex-wrap:wrap;");
            b.OpenElement(i++, "button");
            b.AddAttribute(i++, "type", "button");
            b.AddAttribute(i++, "class", "cfy-btn");
            b.AddAttribute(i++, "disabled", _animadexTesting);
            b.AddAttribute(i++, "onclick", EventCallback.Factory.Create(this, TestAnimadexConnectivity));
            b.AddContent(i++, _animadexTesting ? "测试中…" : "✦ 测试连通");
            b.CloseElement();
            if (!string.IsNullOrWhiteSpace(_animadexTestMessage))
            {
                b.OpenElement(i++, "div");
                b.AddAttribute(i++, "class", "cfy-detect");
                b.AddAttribute(i++, "style", "margin:0;flex:1;");
                b.AddContent(i++, _animadexTestMessage);
                b.CloseElement();
            }
            b.CloseElement();
            b.CloseElement();

            AddHint(b, ref i, "上游：animadex.net（Danbooru 角色标签聚合，MIT 客户端）。在线失败时 findcharacterprompt 自动回到纯本地提示，不阻塞生图");
            b.CloseElement(); // grid-2
        }

        b.CloseElement(); // animadex panel

        // 默认参数
        AddSection(b, ref i, "默认参数");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-panel");

        // 双栏：仅放 select / input 类控件
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-grid-2");

        b.OpenElement(i++, "div");
        AddSelect(b, ref i, "默认方向", Configuration.DefaultOrientation, v => Configuration.DefaultOrientation = v, new[]
        {
            ("portrait", $"竖版 {Configuration.PortraitWidth}×{Configuration.PortraitHeight}"),
            ("landscape", $"横版 {Configuration.LandscapeWidth}×{Configuration.LandscapeHeight}"),
            ("square", $"正方形 {Configuration.SquareWidth}×{Configuration.SquareHeight}")
        });
        AddHint(b, ref i, "AI 未传 orientation 时的默认方向");
        AddInput(b, ref i, "兜底宽度", Configuration.DefaultWidth.ToString(), v =>
        {
            if (int.TryParse(v, out var n)) Configuration.DefaultWidth = n;
        });
        AddInput(b, ref i, "兜底高度", Configuration.DefaultHeight.ToString(), v =>
        {
            if (int.TryParse(v, out var n)) Configuration.DefaultHeight = n;
        });
        b.CloseElement();

        b.OpenElement(i++, "div");
        AddInput(b, ref i, "超时秒数", Configuration.TimeoutSeconds.ToString(), v =>
        {
            if (int.TryParse(v, out var n)) Configuration.TimeoutSeconds = n;
        });
        AddInput(b, ref i, "轮询间隔毫秒", Configuration.PollIntervalMs.ToString(), v =>
        {
            if (int.TryParse(v, out var n)) Configuration.PollIntervalMs = n;
        });
        b.CloseElement();

        b.CloseElement(); // grid-2

        // 全宽区域：选项开关
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "style", "display:flex;flex-wrap:wrap;align-items:center;gap:12px 24px;margin-top:14px;");

        // 自动打开图片
        b.OpenElement(i++, "label");
        b.AddAttribute(i++, "style", "display:inline-flex;align-items:center;gap:8px;cursor:pointer;");
        b.OpenElement(i++, "input");
        b.AddAttribute(i++, "type", "checkbox");
        b.AddAttribute(i++, "checked", Configuration.AutoOpenImage);
        b.AddAttribute(i++, "style", "accent-color:#ec4899;width:16px;height:16px;cursor:pointer;flex-shrink:0;");
        b.AddAttribute(i++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            Configuration.AutoOpenImage = (bool)(e.Value ?? false);
        }));
        b.CloseElement();
        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "style", "font-size:12.5px;color:#9d174d;font-weight:700;white-space:nowrap;");
        b.AddContent(i++, "生完图自动用系统默认图片查看器打开");
        b.CloseElement();
        b.CloseElement();

        // 优先生图
        b.OpenElement(i++, "label");
        b.AddAttribute(i++, "style", "display:inline-flex;align-items:center;gap:8px;cursor:pointer;");
        b.OpenElement(i++, "input");
        b.AddAttribute(i++, "type", "checkbox");
        b.AddAttribute(i++, "checked", Configuration.PriorityImageGen);
        b.AddAttribute(i++, "style", "accent-color:#ec4899;width:16px;height:16px;cursor:pointer;flex-shrink:0;");
        b.AddAttribute(i++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            Configuration.PriorityImageGen = (bool)(e.Value ?? false);
        }));
        b.CloseElement();
        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "style", "font-size:12.5px;color:#9d174d;font-weight:700;white-space:nowrap;");
        b.AddContent(i++, "优先生图：生图开始后等图完再返回（同机 TTS 建议开）");
        b.CloseElement();
        b.CloseElement();

        b.CloseElement(); // 选项开关行

        // 同机 TTS 提示（不依赖语音插件）
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "style", "font-size:11px;color:#b06a8c;font-weight:600;margin-top:8px;line-height:1.45;");
        b.AddContent(i++, "同机本地 TTS：建议开启「优先生图」。默认关，纯生图用户不受影响。本插件不依赖语音插件。");
        b.CloseElement();

        // 条件输入：硬超时上限（在开关行下方，独立一行）
        if (Configuration.PriorityImageGen)
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "style", "display:flex;align-items:center;gap:12px;margin-top:10px;flex-wrap:wrap;");
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "style", "width:180px;flex-shrink:0;");
            AddInput(b, ref i, "硬超时上限（秒）", Configuration.PriorityMaxWaitSeconds.ToString(), v =>
            {
                if (int.TryParse(v, out var n))
                    Configuration.PriorityMaxWaitSeconds = Math.Clamp(n, 30, 1800);
            });
            b.CloseElement();
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "style", "font-size:11px;color:#b06a8c;font-weight:600;");
            b.AddContent(i++, "实际 = min(超时秒数, 本上限)，超时强制结束避免卡死");
            b.CloseElement();
            b.CloseElement();
        }

        b.CloseElement(); // panel

        // 节点映射
        AddSection(b, ref i, "节点映射（高级）");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-panel");
        AddHint(b, ref i, "留空即可自动识别。特殊工作流可点按钮扫描并回填节点 ID");

        b.OpenElement(i++, "div");
        b.OpenElement(i++, "button");
        b.AddAttribute(i++, "type", "button");
        b.AddAttribute(i++, "class", "cfy-btn");
        b.AddAttribute(i++, "onclick", EventCallback.Factory.Create(this, AutoDetectNodes));
        b.AddContent(i++, "✦ 自动识别节点");
        b.CloseElement();
        b.CloseElement();

        if (!string.IsNullOrWhiteSpace(detectMessage))
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cfy-detect");
            b.AddContent(i++, detectMessage);
            b.CloseElement();
        }

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-grid-2");

        b.OpenElement(i++, "div");
        AddInput(b, ref i, "正向提示词节点 ID", Configuration.PositivePromptNodeId, v => Configuration.PositivePromptNodeId = v);
        AddInput(b, ref i, "正向提示词字段名", Configuration.PositivePromptInput, v => Configuration.PositivePromptInput = v);
        AddInput(b, ref i, "分辨率节点 ID", Configuration.ResolutionNodeId, v => Configuration.ResolutionNodeId = v);
        b.CloseElement();

        b.OpenElement(i++, "div");
        AddInput(b, ref i, "负面提示词节点 ID", Configuration.NegativePromptNodeId, v => Configuration.NegativePromptNodeId = v);
        AddInput(b, ref i, "负面提示词字段名", Configuration.NegativePromptInput, v => Configuration.NegativePromptInput = v);
        b.CloseElement();

        b.CloseElement();
        AddHint(b, ref i, "手动节点 ID 仅用于默认工作流；命名工作流会按各自连接关系自动识别");
        b.CloseElement();

        // 高级模式开关
        AddSection(b, ref i, "高级模式");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-advanced-toggle");
        b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, async e =>
        {
            Configuration.AdvancedMode = !Configuration.AdvancedMode;
            if (Configuration.AdvancedMode)
                await LoadNodeOverview();
            StateHasChanged();
        }));
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-advanced-label");
        b.AddContent(i++, "工作流节点概览");
        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "class", "cfy-advanced-badge");
        b.AddContent(i++, "BETA");
        b.CloseElement();
        b.CloseElement();
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", $"cfy-advanced-switch{(Configuration.AdvancedMode ? " active" : "")}");
        b.CloseElement();
        b.CloseElement();
        AddHint(b, ref i, "开启后展示工作流中所有节点类型及关键参数，方便排查问题或手动配置节点映射");

        // AI 节点控制开关
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-advanced-toggle");
        b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e =>
        {
            Configuration.EnableNodeControl = !Configuration.EnableNodeControl;
            StateHasChanged();
        }));
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-advanced-label");
        b.AddContent(i++, "AI 节点控制（高级）");
        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "class", "cfy-advanced-badge");
        b.AddContent(i++, "ADVANCED");
        b.CloseElement();
        b.CloseElement();
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", $"cfy-advanced-switch{(Configuration.EnableNodeControl ? " active" : "")}");
        b.CloseElement();
        b.CloseElement();
        AddHint(b, ref i, "开启后，AI 可直接操控工作流节点参数（更换模型、调整步数/CFG/采样器等）。不开启则保持原有简单模式不受影响。");

        // 隐式注入开关（4.0 新特性：DocumentMode.Implicit / Explicit 切换）
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-advanced-toggle");
        b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e =>
        {
            Configuration.ImplicitInjection = !Configuration.ImplicitInjection;
            StateHasChanged();
        }));
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-advanced-label");
        b.AddContent(i++, "隐式注入（省 token）");
        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "class", "cfy-advanced-badge");
        b.AddContent(i++, "4.0");
        b.CloseElement();
        b.CloseElement();
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", $"cfy-advanced-switch{(Configuration.ImplicitInjection ? " active" : "")}");
        b.CloseElement();
        b.CloseElement();
        AddHint(b, ref i, "开启后函数文档不直接注入系统提示词，AI 需先调用 <comfyuiimagegeneration/> 按需加载（省 token，渐进式）；关闭则为显式注入（默认，功能说明直接可用）。改动需重载模块后生效。");

        if (Configuration.AdvancedMode)
        {
            AddNodeOverview(b, ref i);
        }

        // footer
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cfy-footer");
        b.OpenElement(i++, "span");
        b.AddContent(i++, "ComfyUI × Alife");
        b.CloseElement();
        b.AddContent(i++, "  ·  Doro 的妙妙工具");
        b.CloseElement();

        b.CloseElement(); // content
        b.CloseElement(); // container
        b.CloseElement(); // root
    }

    void AddResoCard(RenderTreeBuilder b, ref int seq, string tag, string size, string name, string hint)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "cfy-reso-card");
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "cfy-reso-shine");
        b.CloseElement();
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "cfy-reso-tag");
        b.AddContent(seq++, tag);
        b.CloseElement();
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "cfy-reso-size");
        b.AddContent(seq++, size);
        b.CloseElement();
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "cfy-reso-name");
        b.AddContent(seq++, name);
        b.CloseElement();
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "cfy-reso-hint");
        b.AddContent(seq++, hint);
        b.CloseElement();
        b.CloseElement();
    }

    void AddSection(RenderTreeBuilder b, ref int seq, string text)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "cfy-section");
        b.AddContent(seq++, text);
        b.CloseElement();
    }

    void AddHint(RenderTreeBuilder b, ref int seq, string text)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "cfy-hint");
        b.AddContent(seq++, text);
        b.CloseElement();
    }

    void AddInput(RenderTreeBuilder b, ref int seq, string label, string value, Action<string> onChange)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "cfy-label");
        b.AddContent(seq++, label);
        b.CloseElement();

        b.OpenComponent<Input<string>>(seq++);
        b.AddAttribute(seq++, "Value", value ?? "");
        b.AddAttribute(seq++, "ValueChanged", EventCallback.Factory.Create<string>(this, onChange));
        b.AddAttribute(seq++, "Style", "width:100%;");
        b.CloseComponent();
    }

    void AddTextArea(RenderTreeBuilder b, ref int seq, string label, string value, Action<string> onChange, int rows = 4)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "cfy-label");
        b.AddContent(seq++, label);
        b.CloseElement();

        b.OpenElement(seq++, "textarea");
        b.AddAttribute(seq++, "class", "ant-input cfy-textarea");
        b.AddAttribute(seq++, "rows", rows);
        b.AddAttribute(seq++, "spellcheck", "false");
        b.AddAttribute(seq++, "value", value ?? "");
        b.AddAttribute(seq++, "oninput",
            EventCallback.Factory.Create<ChangeEventArgs>(this, e => onChange(e.Value?.ToString() ?? "")));
        b.CloseElement();
    }

    void AddSelect(RenderTreeBuilder b, ref int seq, string label, string value, Action<string> onChange, (string val, string text)[] options)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "cfy-label");
        b.AddContent(seq++, label);
        b.CloseElement();

        b.OpenElement(seq++, "select");
        b.AddAttribute(seq++, "class", "cfy-select");
        b.AddAttribute(seq++, "value", value ?? "");
        b.AddAttribute(seq++, "onchange",
            EventCallback.Factory.Create<ChangeEventArgs>(this, e => onChange(e.Value?.ToString() ?? "")));
        foreach (var opt in options)
        {
            b.OpenElement(seq++, "option");
            b.AddAttribute(seq++, "value", opt.val);
            if ((value ?? "") == opt.val)
                b.AddAttribute(seq++, "selected", "selected");
            b.AddContent(seq++, opt.text);
            b.CloseElement();
        }
        b.CloseElement();
    }

    async Task UnloadModels()
    {
        if (_unloadingModel) return;
        _unloadingModel = true;
        _unloadMessage = "正在请求 ComfyUI 卸载模型…";
        StateHasChanged();
        try
        {
            _unloadMessage = await ComfyuiService.UnloadModelsNowAsync(Configuration);
        }
        catch (Exception ex)
        {
            _unloadMessage = "卸载异常: " + ex.Message;
        }
        finally
        {
            _unloadingModel = false;
            StateHasChanged();
        }
    }

    async Task TestDanbooruConnectivity()
    {
        if (_danbooruTesting) return;
        _danbooruTesting = true;
        _danbooruTestMessage = "探测中…";
        StateHasChanged();
        try
        {
            var primary = Configuration.DanbooruSearchPrimaryUrl?.Trim() ?? "";
            var fallback = Configuration.DanbooruSearchFallbackUrl?.Trim() ?? "";
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(primary))
            {
                var (ok, msg, ms) = await DanbooruSearchClient.HealthAsync(primary, 20);
                parts.Add($"主源 {(ok ? "OK" : "FAIL")} {ms}ms — {msg}");
            }
            else
            {
                parts.Add("主源未填写");
            }

            if (!string.IsNullOrWhiteSpace(fallback)
                && !string.Equals(primary?.TrimEnd('/'), fallback.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                var (ok, msg, ms) = await DanbooruSearchClient.HealthAsync(fallback, 25);
                parts.Add($"备用 {(ok ? "OK" : "FAIL")} {ms}ms — {msg}");
            }

            _danbooruTestMessage = string.Join(" | ", parts);
        }
        catch (Exception ex)
        {
            _danbooruTestMessage = "测试异常: " + ex.Message;
        }
        finally
        {
            _danbooruTesting = false;
            StateHasChanged();
        }
    }

    async Task TestAnimadexConnectivity()
    {
        if (_animadexTesting) return;
        _animadexTesting = true;
        _animadexTestMessage = "探测中…";
        StateHasChanged();
        try
        {
            var baseUrl = Configuration.AnimadexBaseUrl?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                _animadexTestMessage = "未填写服务地址";
            }
            else
            {
                var (ok, msg, ms) = await AnimadexClient.HealthAsync(baseUrl, 20);
                _animadexTestMessage = $"{(ok ? "OK" : "FAIL")} {ms}ms — {msg}";
            }
        }
        catch (Exception ex)
        {
            _animadexTestMessage = "测试异常: " + ex.Message;
        }
        finally
        {
            _animadexTesting = false;
            StateHasChanged();
        }
    }

    async Task AutoDetectNodes()
    {
        _scanDetectMessage = null;
        try
        {
            var path = Configuration.WorkflowPath?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(path))
            {
                detectMessage = "请先填写工作流路径";
                StateHasChanged();
                return;
            }

            string full = path;
            if (!File.Exists(full))
            {
                var pluginDir = Path.Combine(AlifePath.StorageFolderPath, "Plugins", "Alife.Plugin.Comfyui");
                try { full = Path.GetFullPath(Path.Combine(pluginDir, path)); } catch { }
            }
            if (!File.Exists(full))
            {
                detectMessage = $"找不到工作流文件: {path}";
                StateHasChanged();
                return;
            }

            var raw = await File.ReadAllTextAsync(full);
            if (JsonNode.Parse(raw) is not JsonObject root)
            {
                detectMessage = "工作流 JSON 解析失败";
                StateHasChanged();
                return;
            }

            var api = ComfyuiWorkflowConverter.ToApiPrompt(root, null);
            var (posId, negId) = ComfyuiWorkflowConverter.FindPositiveAndNegativeIds(api);
            var resId = ComfyuiWorkflowConverter.FindResolutionNodeId(api);

            // 先清空旧结果，避免更换工作流后识别失败却继续使用上一份节点 ID。
            Configuration.PositivePromptNodeId = posId ?? "";
            Configuration.NegativePromptNodeId = negId ?? "";
            Configuration.ResolutionNodeId = resId ?? "";

            if (!string.IsNullOrEmpty(posId) && api[posId] is JsonObject posNode)
            {
                var posInputs = posNode["inputs"] as JsonObject ?? new JsonObject();
                Configuration.PositivePromptInput = ComfyuiWorkflowConverter.ResolvePromptField(posInputs, Configuration.PositivePromptInput);
            }
            if (!string.IsNullOrEmpty(negId) && api[negId] is JsonObject negNode)
            {
                var negInputs = negNode["inputs"] as JsonObject ?? new JsonObject();
                Configuration.NegativePromptInput = ComfyuiWorkflowConverter.ResolvePromptField(negInputs, Configuration.NegativePromptInput);
            }

            detectMessage =
                $"已识别（共 {api.Count} 节点）：\n" +
                $"· 正向 #{posId ?? "?"}（字段 {Configuration.PositivePromptInput}）\n" +
                $"· 负面 #{negId ?? "?"}（字段 {Configuration.NegativePromptInput}）\n" +
                $"· 分辨率 #{resId ?? "?"}" +
                (string.IsNullOrEmpty(posId) ? "\n提示：未识别到正向节点，请手动指定" : "");
        }
        catch (Exception ex)
        {
            detectMessage = $"识别失败: {ex.Message}";
        }
        StateHasChanged();
    }

    void AddEditableResoCard(RenderTreeBuilder b, ref int seq, string tag, string name, string hint,
        int width, Action<int> onWidthChanged, int height, Action<int> onHeightChanged)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "cfy-reso-card");
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "cfy-reso-shine");
        b.CloseElement();
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "cfy-reso-tag");
        b.AddContent(seq++, tag);
        b.CloseElement();
        // editable input row
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "cfy-reso-input-row");
        b.OpenElement(seq++, "input");
        b.AddAttribute(seq++, "class", "cfy-reso-input");
        b.AddAttribute(seq++, "type", "number");
        b.AddAttribute(seq++, "value", width);
        b.AddAttribute(seq++, "min", "64");
        b.AddAttribute(seq++, "max", "4096");
        b.AddAttribute(seq++, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            if (int.TryParse(e.Value?.ToString(), out var n))
                onWidthChanged(Math.Clamp(n, 64, 4096));
        }));
        b.CloseElement();
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "cfy-reso-sep");
        b.AddContent(seq++, "×");
        b.CloseElement();
        b.OpenElement(seq++, "input");
        b.AddAttribute(seq++, "class", "cfy-reso-input");
        b.AddAttribute(seq++, "type", "number");
        b.AddAttribute(seq++, "value", height);
        b.AddAttribute(seq++, "min", "64");
        b.AddAttribute(seq++, "max", "4096");
        b.AddAttribute(seq++, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            if (int.TryParse(e.Value?.ToString(), out var n))
                onHeightChanged(Math.Clamp(n, 64, 4096));
        }));
        b.CloseElement();
        b.CloseElement();
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "cfy-reso-name");
        b.AddContent(seq++, name);
        b.CloseElement();
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "cfy-reso-hint");
        b.AddContent(seq++, hint);
        b.CloseElement();
        b.CloseElement();
    }

    async Task ScanWorkflows()
    {
        detectMessage = null;
        var path = (Configuration?.WorkflowPath ?? "").Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            _scanDetectMessage = "请先在路径框中输入 ComfyUI 目录或工作流目录路径";
            StateHasChanged();
            return;
        }

        var dir = ResolveWorkflowScanDir(path);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            _scanDetectMessage = $"路径无效或目录不存在: {path}";
            StateHasChanged();
            return;
        }

        _scanDir = dir;

        try
        {
            var allFiles = Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly);
            var workflows = new List<string>();
            foreach (var file in allFiles)
            {
                if (await IsComfyuiWorkflow(file))
                    workflows.Add(file);
            }

            _scannedWorkflows = workflows.OrderBy(f => f).ToList();
            _showWorkflowDropdown = _scannedWorkflows.Count > 0;

            _scanDetectMessage = _scannedWorkflows.Count > 0
                ? $"扫描 {dir}\n找到 {_scannedWorkflows.Count} 个工作流，请在下方选择"
                : $"在 {dir}\n未找到有效工作流 .json 文件";
        }
        catch (Exception ex)
        {
            _scanDetectMessage = $"扫描失败: {ex.Message}";
        }
        StateHasChanged();
    }

    /// <summary>
    /// 智能定位工作流扫描目录：识别 ComfyUI / aki 版，自动进 workflows 子目录
    /// </summary>
    static string ResolveWorkflowScanDir(string enteredPath)
    {
        if (!Directory.Exists(enteredPath))
        {
            var parent = Path.GetDirectoryName(enteredPath);
            if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
                return parent;
            return enteredPath;
        }

        // 直接是 ComfyUI 根目录（有 main.py）
        if (File.Exists(Path.Combine(enteredPath, "main.py")))
        {
            var wf = TryGetWorkflowsDir(enteredPath);
            if (wf != null) return wf;
        }

        // aki 版：ComfyUI/main.py 在子目录里
        var inner = Path.Combine(enteredPath, "ComfyUI");
        if (Directory.Exists(inner) && File.Exists(Path.Combine(inner, "main.py")))
        {
            var wf = TryGetWorkflowsDir(inner);
            if (wf != null) return wf;
        }

        // 非 ComfyUI 根目录，直接用用户输入的目录
        return enteredPath;
    }

    static string? TryGetWorkflowsDir(string comfyuiRoot)
    {
        var candidates = new[]
        {
            Path.Combine(comfyuiRoot, "user", "default", "workflows"),
            Path.Combine(comfyuiRoot, "workflows"),
        };
        foreach (var c in candidates)
        {
            if (Directory.Exists(c)) return c;
        }
        return null;
    }

    /// <summary>
    /// 判断 JSON 文件是否为 ComfyUI 工作流（非配置文件等）
    /// </summary>
    static async Task<bool> IsComfyuiWorkflow(string filePath)
    {
        try
        {
            var raw = await File.ReadAllTextAsync(filePath);
            // API 格式：顶层数字键 + class_type
            if (raw.Contains("\"class_type\""))
                return true;
            // UI 格式：last_node_id + nodes + links
            if (raw.Contains("\"last_node_id\"") && raw.Contains("\"nodes\"") && raw.Contains("\"links\""))
                return true;
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 下拉列表显示名：仅取文件名，hover 显示完整路径
    /// </summary>
    static string GetWorkflowDisplayName(string fullPath, string scanDir)
    {
        return Path.GetFileName(fullPath);
    }

    async Task LoadNodeOverview()
    {
        _nodeOverview = new();
        _nodeOverviewError = null;
        try
        {
            var path = (Configuration?.WorkflowPath ?? "").Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                _nodeOverviewError = "请先配置工作流路径";
                return;
            }
            var full = path;
            if (!File.Exists(full))
            {
                var pluginDir = Path.Combine(AlifePath.StorageFolderPath, "Plugins", "Alife.Plugin.Comfyui");
                try { full = Path.GetFullPath(Path.Combine(pluginDir, path)); } catch { }
            }
            if (!File.Exists(full))
            {
                _nodeOverviewError = $"找不到工作流文件: {path}";
                return;
            }

            var raw = await File.ReadAllTextAsync(full);
            if (JsonNode.Parse(raw) is not JsonObject root)
            {
                _nodeOverviewError = "工作流 JSON 解析失败";
                return;
            }

            var api = ComfyuiWorkflowConverter.ToApiPrompt(root, null);
            foreach (var kv in api)
            {
                if (kv.Value is not JsonObject node) continue;
                var ct = node["class_type"]?.GetValue<string>() ?? "?";
                var inputs = node["inputs"] as JsonObject ?? new JsonObject();
                var keyParams = string.Join(", ", inputs
                    .Select(i =>
                    {
                        var valStr = i.Value switch
                        {
                            JsonValue jv => jv.GetValue<object>()?.ToString() ?? "null",
                            JsonArray => "[…]",
                            JsonObject => "{…}",
                            null => "null",
                            _ => "…"
                        };
                        var s = $"{i.Key}: {valStr}";
                        return s.Length > 50 ? s[..47] + "…" : s;
                    })
                    .Take(3));
                if (string.IsNullOrWhiteSpace(keyParams))
                    keyParams = $"{inputs.Count} 个输入";
                _nodeOverview.Add((kv.Key, ct, keyParams));
            }
        }
        catch (Exception ex)
        {
            _nodeOverviewError = $"解析失败: {ex.Message}";
        }
    }

    void AddNodeOverview(RenderTreeBuilder b, ref int seq)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "cfy-node-overview");

        if (!string.IsNullOrWhiteSpace(_nodeOverviewError))
        {
            b.OpenElement(seq++, "div");
            b.AddAttribute(seq++, "style", "padding:12px 16px;color:#be185d;font-size:12px;");
            b.AddContent(seq++, _nodeOverviewError);
            b.CloseElement();
        }
        else if (_nodeOverview.Count == 0)
        {
            b.OpenElement(seq++, "div");
            b.AddAttribute(seq++, "style", "padding:12px 16px;color:#b06a8c;font-size:12px;font-style:italic;");
            b.AddContent(seq++, "正在加载节点信息...");
            b.CloseElement();
        }
        else
        {
            b.OpenElement(seq++, "div");
            b.AddAttribute(seq++, "class", "cfy-node-count");
            b.AddContent(seq++, $"共 {_nodeOverview.Count} 个节点");
            b.CloseElement();

            b.OpenElement(seq++, "div");
            b.AddAttribute(seq++, "style", "max-height:320px;overflow:auto;");
            b.OpenElement(seq++, "table");
            b.AddAttribute(seq++, "class", "cfy-node-table");
            // header
            b.OpenElement(seq++, "thead");
            b.OpenElement(seq++, "tr");
            b.OpenElement(seq++, "th"); b.AddContent(seq++, "节点 ID"); b.CloseElement();
            b.OpenElement(seq++, "th"); b.AddContent(seq++, "类型"); b.CloseElement();
            b.OpenElement(seq++, "th"); b.AddContent(seq++, "关键参数"); b.CloseElement();
            b.CloseElement();
            b.CloseElement();
            // body
            b.OpenElement(seq++, "tbody");
            foreach (var n in _nodeOverview)
            {
                b.OpenElement(seq++, "tr");
                b.OpenElement(seq++, "td");
                b.AddAttribute(seq++, "class", "nid");
                b.AddContent(seq++, n.NodeId);
                b.CloseElement();
                b.OpenElement(seq++, "td");
                b.AddContent(seq++, n.ClassType);
                b.CloseElement();
                b.OpenElement(seq++, "td");
                b.AddAttribute(seq++, "class", "params");
                b.AddContent(seq++, n.KeyParams);
                b.CloseElement();
                b.CloseElement();
            }
            b.CloseElement();
            b.CloseElement();
            b.CloseElement();
        }
        b.CloseElement();
    }

    // ===================== 命名工作流卡片管理 =====================

    static string? WfJsonString(System.Text.Json.Nodes.JsonObject o, string key)
    {
        if (o.TryGetPropertyValue(key, out var node) && node is System.Text.Json.Nodes.JsonValue value
            && value.TryGetValue<string>(out var s))
            return s;
        return null;
    }

    static bool? WfJsonBool(System.Text.Json.Nodes.JsonObject o, string key)
    {
        if (o.TryGetPropertyValue(key, out var node) && node is System.Text.Json.Nodes.JsonValue value
            && value.TryGetValue<bool>(out var b))
            return b;
        return null;
    }

    void LoadWorkflowCards()
    {
        _workflowCards = new();
        var json = Configuration?.NamedWorkflows ?? "[]";
        if (string.IsNullOrWhiteSpace(json)) return;

        try
        {
            var arr = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonArray;
            if (arr != null)
            {
                foreach (var item in arr.OfType<System.Text.Json.Nodes.JsonObject>())
                {
                    _workflowCards.Add(new WorkflowCard
                    {
                        Name = WfJsonString(item, "n") ?? WfJsonString(item, "name") ?? "",
                        Path = WfJsonString(item, "p") ?? WfJsonString(item, "path") ?? "",
                        Enabled = WfJsonBool(item, "e") ?? true,
                        Prefix = WfJsonString(item, "f") ?? WfJsonString(item, "prefix") ?? ""
                    });
                }
            }
        }
        catch { }
    }

    void SaveWorkflowCards()
    {
        var arr = new System.Text.Json.Nodes.JsonArray();
        foreach (var card in _workflowCards)
        {
            arr.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["n"] = card.Name,
                ["p"] = card.Path,
                ["e"] = card.Enabled,
                ["f"] = card.Prefix
            });
        }
        Configuration.NamedWorkflows = arr.ToJsonString();
    }

    void AddWorkflowCard()
    {
        _workflowCards.Add(new WorkflowCard { Name = "新工作流", Path = "", Enabled = true });
        SaveWorkflowCards();
        StateHasChanged();
    }

    void RemoveWorkflowCard(int index)
    {
        if (index < 0 || index >= _workflowCards.Count) return;
        _workflowCards.RemoveAt(index);
        _activeScanCardIndex = -1;
        SaveWorkflowCards();
        StateHasChanged();
    }

    void UpdateWorkflowCardName(int index, string name)
    {
        if (index < 0 || index >= _workflowCards.Count) return;
        _workflowCards[index] = _workflowCards[index] with { Name = name };
        SaveWorkflowCards();
    }

    void UpdateWorkflowCardPath(int index, string path)
    {
        if (index < 0 || index >= _workflowCards.Count) return;
        _workflowCards[index] = _workflowCards[index] with { Path = path };
        SaveWorkflowCards();
    }

    void UpdateWorkflowCardPrefix(int index, string prefix)
    {
        if (index < 0 || index >= _workflowCards.Count) return;
        _workflowCards[index] = _workflowCards[index] with { Prefix = prefix };
        SaveWorkflowCards();
    }

    void ToggleWorkflowCardEnabled(int index)
    {
        if (index < 0 || index >= _workflowCards.Count) return;
        _workflowCards[index] = _workflowCards[index] with { Enabled = !_workflowCards[index].Enabled };
        SaveWorkflowCards();
        StateHasChanged();
    }

    void ToggleWorkflowScan(int index)
    {
        _activeScanCardIndex = _activeScanCardIndex == index ? -1 : index;
        StateHasChanged();
    }

    // ===================== 提示词预设卡片管理 =====================

    void LoadPresetCards()
    {
        _presetCards = new();
        var path = ComfyuiService.GetPresetFilePath();
        if (!File.Exists(path)) return;
        try
        {
            var json = System.IO.File.ReadAllText(path, System.Text.Encoding.UTF8);
            var arr = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonArray;
            if (arr != null)
            {
                foreach (var item in arr.OfType<System.Text.Json.Nodes.JsonObject>())
                {
                    _presetCards.Add(new PresetCard
                    {
                        Name = item["n"]?.GetValue<string>() ?? item["name"]?.GetValue<string>() ?? "",
                        Content = item["c"]?.GetValue<string>() ?? item["content"]?.GetValue<string>() ?? ""
                    });
                }
            }
        }
        catch { }
    }

    void SavePresetCards()
    {
        // 始终写入用户数据目录，避免写回 Plugins 后被市场更新清掉
        var path = System.IO.Path.Combine(
            ComfyuiService.GetUserDataDirectory(), "prompt-presets.json");
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir) && !System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);

            var arr = new System.Text.Json.Nodes.JsonArray();
            foreach (var card in _presetCards)
            {
                arr.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["n"] = card.Name,
                    ["c"] = card.Content
                });
            }
            System.IO.File.WriteAllText(path, arr.ToJsonString(), System.Text.Encoding.UTF8);
        }
        catch { }
    }

    void AddPresetCard()
    {
        var name = _newPresetName?.Trim() ?? "";
        var content = _newPresetContent?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name))
        {
            StateHasChanged();
            return;
        }
        if (name.Length > 60) name = name[..60];
        if (content.Length > 4000) content = content[..4000];

        // 同名覆盖
        _presetCards = _presetCards
            .Where(c => !c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        _presetCards.Add(new PresetCard { Name = name, Content = content });
        _newPresetName = "";
        _newPresetContent = "";
        SavePresetCards();
        StateHasChanged();
    }

    void RemovePresetCard(int index)
    {
        if (index < 0 || index >= _presetCards.Count) return;
        _presetCards.RemoveAt(index);
        _activePresetIndex = -1;
        SavePresetCards();
        StateHasChanged();
    }

    void UpdatePresetCardName(int index, string name)
    {
        if (index < 0 || index >= _presetCards.Count) return;
        _presetCards[index] = _presetCards[index] with { Name = name };
        SavePresetCards();
    }

    void UpdatePresetCardContent(int index, string content)
    {
        if (index < 0 || index >= _presetCards.Count) return;
        _presetCards[index] = _presetCards[index] with { Content = content };
        SavePresetCards();
    }

    void TogglePresetExpand(int index)
    {
        _activePresetIndex = _activePresetIndex == index ? -1 : index;
        StateHasChanged();
    }
}
