// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.Json;

namespace QuickLook.Plugin.OfficeViewer;

/// <summary>
/// v6.7.1: a document's outline is drawn inside the WebView instead of in a
/// WPF column. The rail can therefore float over the paper, collapse without
/// taking width away from the reader, and be resized on its right edge.
/// </summary>
internal static class OfficePanelNavigation
{
    private const string BodyEnd = "</body>";

    internal static string Inject(
        string html,
        IReadOnlyList<OfficeNavEntry> navigation,
        int selectedIndex)
    {
        if (string.IsNullOrEmpty(html) || navigation is null || navigation.Count == 0)
            return html;

        var bodyEnd = html.LastIndexOf(BodyEnd, StringComparison.OrdinalIgnoreCase);
        if (bodyEnd < 0)
            return html;

        var selected = Math.Clamp(selectedIndex, 0, navigation.Count - 1);
        var navigationHtml = BuildNavigation(navigation, selected);
        return html.Insert(bodyEnd, navigationHtml);
    }

    internal static bool TryReadSelection(string message, out int index)
        => TryReadIndex(message, "office-navigation", out index);

    internal static bool TryReadPosition(string message, out int index)
        => TryReadIndex(message, "office-position", out index);

    private static bool TryReadIndex(string message, string expectedType, out int index)
    {
        index = -1;
        if (string.IsNullOrWhiteSpace(message))
            return false;

        try
        {
            using var json = JsonDocument.Parse(message);
            if (!json.RootElement.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String ||
                type.GetString() != expectedType ||
                !json.RootElement.TryGetProperty("index", out var value) ||
                !value.TryGetInt32(out index))
            {
                index = -1;
                return false;
            }

            return index >= 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string BuildNavigation(IReadOnlyList<OfficeNavEntry> navigation, int selected)
    {
        var sb = new StringBuilder(navigation.Count * 160 + 6000);
        sb.Append(
            """
            <style id="ql-office-navigation-style">
            #ql-office-navigation-toggle{position:fixed;z-index:2147483647;top:22px;left:22px;width:32px;height:32px;
            display:flex;align-items:center;justify-content:center;box-sizing:border-box;border:1px solid rgba(255,255,255,.18);
            border-radius:9px;background:rgba(31,35,42,.88);color:#fff;font:600 18px/1 'Segoe UI Symbol','Segoe UI',sans-serif;
            cursor:pointer;box-shadow:0 5px 18px rgba(0,0,0,.28);backdrop-filter:blur(14px) saturate(1.25);-webkit-backdrop-filter:blur(14px) saturate(1.25)}
            #ql-office-navigation-toggle:hover{background:rgba(48,54,64,.94)}
            #ql-office-navigation-toggle:focus-visible,#ql-office-navigation-resize:focus-visible{outline:2px solid #8AB4F8;outline-offset:2px}
            #ql-office-navigation{position:fixed;z-index:2147483646;top:14px;bottom:14px;left:14px;width:260px;
            max-width:calc(100vw - 28px);display:flex;flex-direction:column;box-sizing:border-box;overflow:hidden;
            border:1px solid rgba(255,255,255,.16);border-radius:14px;background:rgba(30,34,41,.86);color:#F7F8FA;
            box-shadow:0 16px 44px rgba(0,0,0,.32);backdrop-filter:blur(22px) saturate(1.25);
            -webkit-backdrop-filter:blur(22px) saturate(1.25);transition:opacity .16s ease,transform .18s ease,visibility .18s;
            transform:translateX(0)}
            #ql-office-navigation[data-collapsed="true"]{opacity:0;visibility:hidden;pointer-events:none;transform:translateX(calc(-100% - 22px))}
            .ql-office-navigation-header{flex:0 0 55px;display:flex;align-items:center;padding:0 18px 0 56px;
            border-bottom:1px solid rgba(255,255,255,.1);font:600 11px/1 'Segoe UI',sans-serif;letter-spacing:.12em;
            text-transform:uppercase;color:rgba(255,255,255,.62)}
            .ql-office-navigation-list{flex:1 1 auto;min-height:0;overflow:auto;padding:7px 7px 10px}
            .ql-office-navigation-item{display:block;width:100%;box-sizing:border-box;margin:1px 0;padding:8px 10px;
            padding-left:calc(10px + var(--ql-level,0) * 14px);border:0;border-radius:8px;background:transparent;
            color:rgba(255,255,255,.82);font:400 12.5px/1.35 'Segoe UI',sans-serif;text-align:left;cursor:pointer;
            white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
            .ql-office-navigation-item:hover{background:rgba(255,255,255,.09);color:#fff}
            .ql-office-navigation-item[aria-current="true"]{background:rgba(255,255,255,.16);color:#fff;box-shadow:inset 2px 0 #8AB4F8}
            .ql-office-navigation-list::-webkit-scrollbar{width:7px}
            .ql-office-navigation-list::-webkit-scrollbar-thumb{background:rgba(255,255,255,.22);border-radius:4px}
            #ql-office-navigation-resize{position:absolute;top:10px;right:0;bottom:10px;width:10px;cursor:ew-resize;
            touch-action:none;border:0;background:transparent;padding:0}
            #ql-office-navigation-resize::after{content:"";position:absolute;top:0;right:4px;bottom:0;width:1px;
            background:rgba(255,255,255,.16);opacity:0;transition:opacity .12s}
            #ql-office-navigation-resize:hover::after,#ql-office-navigation-resize:focus-visible::after{opacity:1}
            </style>
            <button id="ql-office-navigation-toggle" type="button" title="Show or hide navigation"
            aria-label="Show or hide navigation" aria-controls="ql-office-navigation" aria-expanded="true">☰</button>
            <aside id="ql-office-navigation" aria-label="Document navigation" data-collapsed="false">
            <div class="ql-office-navigation-header">Contents</div><div class="ql-office-navigation-list">
            """);

        for (var i = 0; i < navigation.Count; i++)
        {
            var entry = navigation[i];
            var level = Math.Clamp(entry?.Level ?? 0, 0, 12);
            var label = WebUtility.HtmlEncode(entry?.Label ?? string.Empty);
            sb.Append("<button type=\"button\" class=\"ql-office-navigation-item\" data-index=\"")
              .Append(i)
              .Append("\" style=\"--ql-level:")
              .Append(level)
              .Append("\" aria-current=\"")
              .Append(i == selected ? "true" : "false")
              .Append("\" title=\"")
              .Append(label)
              .Append("\">")
              .Append(label)
              .Append("</button>");
        }

        sb.Append(
            """
            </div><button id="ql-office-navigation-resize" type="button" role="separator"
            aria-label="Resize navigation" aria-orientation="vertical"></button></aside>
            <script>
            (()=>{
              const nav=document.getElementById('ql-office-navigation');
              const toggle=document.getElementById('ql-office-navigation-toggle');
              const resize=document.getElementById('ql-office-navigation-resize');
              if(!nav||!toggle||!resize)return;
              const list=nav.querySelector('.ql-office-navigation-list');
              const items=Array.from(nav.querySelectorAll('.ql-office-navigation-item'));
              const minWidth=180;
              const maxWidth=()=>Math.max(minWidth,Math.min(520,innerWidth*.58));
              const widthKey='ql-office-navigation-width';
              const collapsedKey='ql-office-navigation-collapsed';

              function readStorage(key){try{return localStorage.getItem(key)}catch{return null}}
              function writeStorage(key,value){try{localStorage.setItem(key,value)}catch{}}
              function select(index){
                const active=items[index];
                items.forEach((item,i)=>item.setAttribute('aria-current',i===index?'true':'false'));
                if(!active||!list)return;
                const top=active.offsetTop;
                const bottom=top+active.offsetHeight;
                if(top<list.scrollTop)list.scrollTop=top;
                else if(bottom>list.scrollTop+list.clientHeight)list.scrollTop=bottom-list.clientHeight;
              }
              function setCollapsed(collapsed,remember){
                nav.dataset.collapsed=collapsed?'true':'false';
                toggle.setAttribute('aria-expanded',collapsed?'false':'true');
                if(remember)writeStorage(collapsedKey,collapsed?'1':'0');
              }
              function setWidth(width){
                nav.style.width=Math.max(minWidth,Math.min(maxWidth(),width))+'px';
              }

              items.forEach((item,index)=>item.addEventListener('click',()=>{
                select(index);
                try{chrome.webview.postMessage(JSON.stringify({type:'office-navigation',index}))}catch{}
              }));
              toggle.addEventListener('click',()=>setCollapsed(nav.dataset.collapsed!=='true',true));

              const savedWidth=Number(readStorage(widthKey));
              if(Number.isFinite(savedWidth)&&savedWidth>0)setWidth(savedWidth);
              setCollapsed(readStorage(collapsedKey)==='1',false);

              let dragStart=null;
              resize.addEventListener('pointerdown',event=>{
                dragStart={x:event.clientX,width:nav.getBoundingClientRect().width};
                resize.setPointerCapture(event.pointerId);
                document.body.style.userSelect='none';
                event.preventDefault();
              });
              resize.addEventListener('pointermove',event=>{
                if(!dragStart)return;
                setWidth(dragStart.width+event.clientX-dragStart.x);
              });
              const finishDrag=event=>{
                if(!dragStart)return;
                dragStart=null;
                document.body.style.userSelect='';
                writeStorage(widthKey,String(nav.getBoundingClientRect().width));
                if(event&&resize.hasPointerCapture(event.pointerId))resize.releasePointerCapture(event.pointerId);
              };
              resize.addEventListener('pointerup',finishDrag);
              resize.addEventListener('pointercancel',finishDrag);
              resize.addEventListener('keydown',event=>{
                if(event.key!=='ArrowLeft'&&event.key!=='ArrowRight')return;
                setWidth(nav.getBoundingClientRect().width+(event.key==='ArrowRight'?16:-16));
                writeStorage(widthKey,String(nav.getBoundingClientRect().width));
                event.preventDefault();
              });
              addEventListener('resize',()=>setWidth(nav.getBoundingClientRect().width));
              window.quickLookOfficeNavigation={select};
            })();
            </script>
            """);

        return sb.ToString();
    }
}
