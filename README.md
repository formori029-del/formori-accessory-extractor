# FORMORI Accessory Extractor (VPM)

의상에서 리본·브로치·목걸이 같은 악세사리만 골라 새 메쉬와 프리팹으로 분리하는 Unity 에디터 툴입니다.
필요한 본·PhysBone·Constraint는 함께 복사되고 원본은 그대로 남습니다.

衣装からアクセサリーだけを選び、新しいメッシュとプレハブに分離する Unity エディターツールです。
Extracts accessories from outfits into their own mesh and prefab; bones, PhysBones and constraints come along.

## VCC에 추가 / VCCに追加 / Add to VCC

VCC → Settings → Packages → **Add Repository** →

```
https://formori029-del.github.io/formori-accessory-extractor/index.json
```

또는 이 링크를 누르면 바로 추가됩니다: `vcc://vpm/addRepo?url=https://formori029-del.github.io/formori-accessory-extractor/index.json`

이 주소는 FORMORI 툴 공용 목록(FORMORI Tools)이라 **Blink Ears** 도 같이 뜹니다 (`source.json` 의 githubRepos).

그 다음 프로젝트의 Manage Project에서 **FORMORI Accessory Extractor**를 추가합니다.
unitypackage로 설치했던 `Assets/AccessoryPrefabMaker` 폴더는 VCC가 자동으로 정리합니다.

메뉴: `FORMORI > Accessory Extractor`

- 패키지: `Packages/com.formori.accessory-extractor`
- 릴리스: GitHub Actions → **Build Release** 실행 (package.json의 version으로 태그·zip 생성)
- 목록: 릴리스 후 **Build Repo Listing**이 GitHub Pages에 index.json을 자동 게시
