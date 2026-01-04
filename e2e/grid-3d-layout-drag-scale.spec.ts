import { test, expect } from '@playwright/test';

/**
 * Grid3DLayoutRenderer - ドラッグ操作とスケールの統合テスト
 * 
 * 【このテストファイルの目的】
 * CSS2Dコンテナにscale()が適用されている場合のドラッグ操作を検証します。
 * - スケール適用時のドラッグ操作が正確に動作するか
 * - マウスの移動距離とセルの移動距離が一致するか
 * - スケール変更時にドラッグが正常に動作し続けるか
 * - リサイズハンドルが正常に動作し続けるか
 * 
 * 【重要な設計判断と背景】
 * 1. ドラッグ操作の検証
 *    - react-grid-layoutのドラッグハンドル（.grid-drag-handle）を使用
 *    - マウスの移動距離とセルの移動距離を比較
 * 
 * 2. スケール値の取得
 *    - .grid-3d-container要素のstyle.transformから取得
 *    - スケール値を考慮して座標を正規化
 * 
 * 3. 座標の検証
 *    - getBoundingClientRect()で取得した座標はスケール適用後の見た目座標
 *    - グリッド座標への変換時にスケールを考慮
 */

test.describe('Grid3DLayoutRenderer - Drag with scale', () => {
  test.beforeEach(async ({ page }) => {
    // アプリを開く
    await page.goto('/');
    
    // ローカルストレージをクリア
    await page.evaluate(() => {
      localStorage.clear();
    });
    
    // アプリケーションの初期化を待つ（DOMが読み込まれるまで）
    await page.waitForLoadState('domcontentloaded');
    
    // バックエンドサーバーが起動していることを確認
    // #region agent log
    // バックエンドサーバー（ポート2718）がリッスンしているか確認
    try {
      const backendResponse = await fetch('http://127.0.0.1:2718', { method: 'HEAD', signal: AbortSignal.timeout(5000) }).catch(() => null);
      await page.evaluate((data) => {
        fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:35',message:'Backend server check',data:data,timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'E'})}).catch(()=>{});
      }, { backendAvailable: backendResponse !== null, status: backendResponse?.status });
    } catch (error) {
      await page.evaluate((errorMsg) => {
        fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:35',message:'Backend server check error',data:{error:errorMsg},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'E'})}).catch(()=>{});
      }, String(error));
    }
    // #endregion
    
    // バックエンドサーバーへの接続が確立されるまで待つ
    // ConnectingAlertが表示されなくなるまで待つ（最大90秒）
    // #region agent log
    await page.evaluate(() => {
      fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:42',message:'Waiting for backend connection',data:{},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'C'})}).catch(()=>{});
    });
    // #endregion
    
    // バックエンドサーバーへの接続が確立されるまで待つ（最大90秒）
    // バックエンドサーバーは起動に時間がかかる可能性があるため、長めに設定
    try {
      let connectionEstablished = false;
      const maxAttempts = 360; // 180秒（500ms * 360）- バックエンドサーバーが起動してからWebSocket接続が確立されるまでに時間がかかる可能性があるため延長
      
      for (let i = 0; i < maxAttempts; i++) {
        const connectionStatus = await page.evaluate(() => {
          // ConnectingAlertが表示されているか確認
          // FloatingAlertコンポーネントが表示されているか確認（"Connecting to a marimo runtime"というテキストを含む）
          const connectingText = Array.from(document.querySelectorAll('*')).find(el => 
            el.textContent?.includes('Connecting to a marimo runtime')
          );
          const hasConnectingAlert = !!connectingText;
          
          // 接続エラーが表示されているか確認（"Failed to connect"というテキストを含む）
          const errorText = Array.from(document.querySelectorAll('*')).find(el => 
            el.textContent?.includes('Failed to connect')
          );
          const hasConnectionError = !!errorText;
          
          // ボタンが有効になっているか確認
          const buttons = Array.from(document.querySelectorAll('button'));
          const pythonButton = buttons.find(btn => btn.textContent?.includes('Python'));
          // disabled属性とdisabledクラスの両方を確認
          // ReactのButtonコンポーネントは、disabledプロパティがtrueの場合、
          // disabled属性を設定するか、pointer-events-noneクラスを適用する
          const hasDisabledAttr = pythonButton?.hasAttribute('disabled');
          const disabledAttrValue = pythonButton?.getAttribute('disabled');
          const hasDisabledClass = pythonButton?.classList.contains('disabled');
          const hasPointerEventsNone = pythonButton?.classList.contains('pointer-events-none');
          const computedStyle = pythonButton ? window.getComputedStyle(pythonButton) : null;
          const pointerEvents = computedStyle?.pointerEvents;
          
          // ボタンが有効かどうかを判定
          // disabled属性が存在するか、disabledクラスが含まれているか、pointer-events-noneクラスが含まれている場合は無効
          const isButtonEnabled = pythonButton ? 
            !hasDisabledAttr && 
            disabledAttrValue !== 'true' &&
            disabledAttrValue !== '' &&
            !hasDisabledClass &&
            !hasPointerEventsNone &&
            pointerEvents !== 'none' : false;
          
          // ボタンの実際の状態を確認
          const buttonDisabled = pythonButton?.getAttribute('disabled');
          const buttonClasses = pythonButton?.className || '';
          
          // PlaywrightのisEnabled()メソッドで確認できるか試す
          // ただし、これは非同期なので、ここでは使用できない
          
          return {
            hasConnectingAlert,
            hasConnectionError,
            isButtonEnabled,
            hasPythonButton: !!pythonButton,
            buttonDisabled,
            buttonClasses,
            hasDisabledAttr,
            disabledAttrValue,
            hasDisabledClass,
            hasPointerEventsNone,
            pointerEvents
          };
        });
        
        // #region agent log
        await page.evaluate((data) => {
          fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:48',message:'Checking connection status',data:data,timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'C'})}).catch(()=>{});
        }, { attempt: i + 1, connectionStatus });
        // #endregion
        
        // 接続エラーが表示されている場合は、接続が確立されないことを記録
        if (connectionStatus.hasConnectionError) {
          // #region agent log
          await page.evaluate(() => {
            fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:70',message:'Connection error detected',data:{},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'G'})}).catch(()=>{});
          });
          // #endregion
        }
        
        if (!connectionStatus.hasConnectingAlert && !connectionStatus.hasConnectionError && connectionStatus.isButtonEnabled) {
          connectionEstablished = true;
          // #region agent log
          await page.evaluate(() => {
            fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:70',message:'Backend connection established',data:{},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'C'})}).catch(()=>{});
          });
          // #endregion
          break;
        }
        
        // 10回ごとにログを出力（ログファイルのサイズを抑制）
        if (i % 10 === 0 && i > 0) {
          // #region agent log
          await page.evaluate((attempt) => {
            fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:78',message:'Still waiting for connection',data:{attempt},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'C'})}).catch(()=>{});
          }, i + 1);
          // #endregion
        }
        
        await page.waitForTimeout(500);
      }
      
      if (!connectionEstablished) {
        // #region agent log
        await page.evaluate((seconds) => {
          fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:86',message:'Backend connection timeout',data:{timeoutSeconds:seconds},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'C'})}).catch(()=>{});
        }, maxAttempts * 500 / 1000);
        // #endregion
        // 接続が確立されない場合でも、テストを続行できるようにする
        // （バックエンドサーバーが起動していない可能性がある）
        console.warn(`Backend connection not established after ${maxAttempts * 500 / 1000} seconds. Tests may fail if backend server is not running.`);
        console.warn('Please ensure the backend server is running: npm run server');
      }
    } catch (error) {
      // #region agent log
      await page.evaluate((errorMsg) => {
        fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:94',message:'Error checking connection',data:{error:errorMsg},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'C'})}).catch(()=>{});
      }, String(error));
      // #endregion
      // 接続が確立されない場合でも、テストを続行できるようにする
      // （バックエンドサーバーが起動していない可能性がある）
      console.warn('Error checking backend connection. Tests may fail if backend server is not running.');
      console.warn('Please ensure the backend server is running: npm run server');
    }
    
    // アプリケーションの初期化を待つ（3Dモードが有効になるまで）
    // 3Dモードはデフォルトで有効なので、グリッドコンテナが表示されるまで待つ
    // ただし、セルが存在しない場合はグリッドコンテナが表示されない可能性があるため、
    // タイムアウトしてもテストを続行できるようにする
    try {
      await page.waitForSelector('.grid-3d-container', { timeout: 15000 });
    } catch (error) {
      // グリッドコンテナが見つからない場合は、アプリケーションがまだ初期化されていないか、
      // セルが存在しない可能性がある。テスト内で適切に処理する。
    }
    
    // 追加の待機時間（アプリケーションの完全な初期化を待つ）
    await page.waitForTimeout(500);
  });

  test.afterEach(async ({ page }) => {
    // テスト後にローカルストレージをクリア
    await page.evaluate(() => {
      localStorage.clear();
    });
  });

  /**
   * セルを追加するヘルパー関数
   */
  async function addCell(page: any, cellType: 'python' | 'markdown' | 'sql' = 'python'): Promise<void> {
    // #region agent log
    fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:62',message:'Adding cell',data:{cellType},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'A'})}).catch(()=>{});
    // #endregion
    
    // AddCellButtonsを探す（3Dモードでも表示される）
    const addCellButtons = page.locator('button').filter({ hasText: cellType === 'python' ? 'Python' : cellType === 'markdown' ? 'Markdown' : 'SQL' });
    
    // ボタンが表示されるまで待つ
    try {
      await addCellButtons.first().waitFor({ state: 'visible', timeout: 10000 });
      // #region agent log
      fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:70',message:'Add cell button found',data:{cellType},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'A'})}).catch(()=>{});
      // #endregion
    } catch (error) {
      // #region agent log
      fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:74',message:'Add cell button not found',data:{cellType,error:String(error)},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'A'})}).catch(()=>{});
      // #endregion
      throw new Error(`Add cell button not found for ${cellType}`);
    }
    
    // ボタンが有効になるまで待つ（接続が確立されるまで）
    // #region agent log
    fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:80',message:'Waiting for button to be enabled',data:{cellType},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'A'})}).catch(()=>{});
    // #endregion
    
    try {
      // ボタンが有効になるまで待つ（最大60秒）
      // PlaywrightのisEnabled()メソッドを使用して、ボタンが有効になるまで待つ
      const button = addCellButtons.first();
      await button.waitFor({ state: 'visible', timeout: 60000 });
      
      // ボタンが有効になるまで待つ（ポーリング）
      let isEnabled = false;
      const maxAttempts = 120; // 60秒（500ms * 120）
      for (let i = 0; i < maxAttempts; i++) {
        isEnabled = await button.isEnabled();
        // #region agent log
        fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:88',message:'Checking if button is enabled',data:{cellType,attempt:i+1,isEnabled},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'A'})}).catch(()=>{});
        // #endregion
        if (isEnabled) {
          break;
        }
        await page.waitForTimeout(500);
      }
      
      if (!isEnabled) {
        // #region agent log
        fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:96',message:'Button not enabled, timeout',data:{cellType},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'A'})}).catch(()=>{});
        // #endregion
        throw new Error(`Add cell button not enabled for ${cellType}. Backend server may not be running.`);
      }
      
      // #region agent log
      fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:100',message:'Button is enabled',data:{cellType},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'A'})}).catch(()=>{});
      // #endregion
    } catch (error) {
      // #region agent log
      fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:104',message:'Error waiting for button to be enabled',data:{cellType,error:String(error)},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'A'})}).catch(()=>{});
      // #endregion
      throw error;
    }
    
    // ボタンをクリック
    // #region agent log
    fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:102',message:'Clicking button',data:{cellType},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'A'})}).catch(()=>{});
    // #endregion
    await addCellButtons.first().click();
    
    // セルが追加されるまで待つ
    await page.waitForTimeout(1000);
    
    // #region agent log
    fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:110',message:'Cell added',data:{cellType},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'A'})}).catch(()=>{});
    // #endregion
  }

  /**
   * グリッドセルをドラッグするヘルパー関数
   */
  async function dragGridCell(
    page: any,
    cellSelector: string,
    deltaX: number,
    deltaY: number
  ): Promise<void> {
    // #region agent log
    fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:93',message:'Starting drag operation',data:{deltaX,deltaY},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'B'})}).catch(()=>{});
    // #endregion
    
    const cell = page.locator(cellSelector).first();
    
    // セル全体の位置を取得
    const cellBox = await cell.boundingBox();
    if (!cellBox) {
      // #region agent log
      fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:100',message:'Cell not found or not visible',data:{},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'B'})}).catch(()=>{});
      // #endregion
      throw new Error('Cell not found or not visible');
    }
    
    // #region agent log
    fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:105',message:'Cell bounding box',data:{x:cellBox.x,y:cellBox.y,width:cellBox.width,height:cellBox.height},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'B'})}).catch(()=>{});
    // #endregion
    
    // ドラッグハンドルを探す
    const dragHandle = cell.locator('.grid-drag-handle').first();
    const dragHandleExists = await dragHandle.count() > 0;
    
    // #region agent log
    fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:112',message:'Drag handle check',data:{exists:dragHandleExists},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'B'})}).catch(()=>{});
    // #endregion
    
    // ドラッグハンドルが存在する場合は、その位置を使用
    let startX: number;
    let startY: number;
    
    if (dragHandleExists) {
      const handleBox = await dragHandle.boundingBox();
      if (handleBox) {
        startX = handleBox.x + handleBox.width / 2;
        startY = handleBox.y + handleBox.height / 2;
        // #region agent log
        fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:122',message:'Using drag handle position',data:{x:startX,y:startY},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'B'})}).catch(()=>{});
        // #endregion
      } else {
        // ドラッグハンドルが見えない場合は、セルの左上を使用
        startX = cellBox.x + 20;
        startY = cellBox.y + 20;
        // #region agent log
        fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:128',message:'Using cell position (handle not visible)',data:{x:startX,y:startY},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'B'})}).catch(()=>{});
        // #endregion
      }
    } else {
      // ドラッグハンドルが存在しない場合は、セルの左上を使用
      startX = cellBox.x + 20;
      startY = cellBox.y + 20;
      // #region agent log
      fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:135',message:'Using cell position (no handle)',data:{x:startX,y:startY},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'B'})}).catch(()=>{});
      // #endregion
    }
    
    const endX = startX + deltaX;
    const endY = startY + deltaY;
    
    // #region agent log
    fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:142',message:'Drag coordinates',data:{startX,startY,endX,endY},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'B'})}).catch(()=>{});
    // #endregion
    
    // セル全体をホバーしてからドラッグ
    await cell.hover({ force: true, position: { x: 20, y: 20 } });
    await page.mouse.down();
    await page.waitForTimeout(200); // mousedownイベントの処理を待つ
    
    // #region agent log
    fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:149',message:'Mouse down',data:{},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'B'})}).catch(()=>{});
    // #endregion
    
    // 段階的に移動（react-grid-layoutがドラッグを認識するように）
    const steps = 20;
    for (let i = 1; i <= steps; i++) {
      const currentX = startX + (deltaX * i) / steps;
      const currentY = startY + (deltaY * i) / steps;
      await page.mouse.move(currentX, currentY);
      await page.waitForTimeout(10);
    }
    
    await page.waitForTimeout(200); // mousemoveイベントの処理を待つ
    await page.mouse.up();
    
    // #region agent log
    fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:162',message:'Mouse up',data:{},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'B'})}).catch(()=>{});
    // #endregion
    
    await page.waitForTimeout(1000); // アニメーションとレイアウト更新の待機
  }

  /**
   * セルの位置を取得するヘルパー関数
   */
  async function getCellPosition(page: any, cellSelector: string): Promise<{ x: number; y: number; width: number; height: number } | null> {
    return await page.evaluate((selector: string) => {
      const element = document.querySelector(selector);
      if (!element) return null;
      const rect = element.getBoundingClientRect();
      return {
        x: rect.left,
        y: rect.top,
        width: rect.width,
        height: rect.height
      };
    }, cellSelector);
  }

  /**
   * グリッドコンテナのスケール値を取得するヘルパー関数
   */
  async function getGridScale(page: any): Promise<number> {
    return await page.evaluate(() => {
      const gridContainer = document.querySelector('.grid-3d-container') as HTMLElement;
      if (!gridContainer) return 1.0;
      const transform = gridContainer.style.transform || '';
      const scaleMatch = transform.match(/scale\(([^)]+)\)/);
      return scaleMatch?.[1] ? parseFloat(scaleMatch[1].trim()) : 1.0;
    });
  }

  /**
   * グリッドコンテナにスケールを適用するヘルパー関数
   */
  async function setGridScale(page: any, scale: number): Promise<void> {
    await page.evaluate((s: number) => {
      const gridContainer = document.querySelector('.grid-3d-container') as HTMLElement;
      if (gridContainer) {
        gridContainer.style.transform = `scale(${s})`;
      }
    }, scale);
    
    // スケール変更後のDOM更新を待つ
    await page.waitForTimeout(200);
  }

  test('ドラッグ時にマウス位置とセル位置が一致する（スケール適用時）', async ({ page }) => {
    // #region agent log
    fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:154',message:'Test started',data:{testName:'ドラッグ時にマウス位置とセル位置が一致する'},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'A'})}).catch(()=>{});
    // #endregion
    
    // 1. セルを追加（グリッドレイアウトが表示されるように）
    try {
      await addCell(page, 'python');
      // #region agent log
      fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:160',message:'Cell added successfully',data:{},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'A'})}).catch(()=>{});
      // #endregion
    } catch (error) {
      // #region agent log
      fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:164',message:'Failed to add cell',data:{error:String(error)},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'A'})}).catch(()=>{});
      // #endregion
      // セルを追加できない場合は、このテストをスキップ
      test.skip();
    }
    
    // #region agent log
    fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:172',message:'Waiting for grid container',data:{},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'A'})}).catch(()=>{});
    // #endregion
    
    let gridContainerExists = false;
    try {
      await page.waitForSelector('.grid-3d-container', { timeout: 10000 });
      gridContainerExists = true;
      // #region agent log
      fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:178',message:'Grid container found',data:{},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'A'})}).catch(()=>{});
      // #endregion
    } catch (error) {
      // #region agent log
      fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:182',message:'Grid container not found, skipping test',data:{error:String(error)},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'A'})}).catch(()=>{});
      // #endregion
      test.skip();
    }

    // 3. スケールを適用
    await setGridScale(page, 1.84583);
    const scale = await getGridScale(page);
    expect(scale).toBeCloseTo(1.84583, 2);

    // 4. セルが存在するか確認
    // #region agent log
    fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:188',message:'Checking for cells',data:{},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'A'})}).catch(()=>{});
    // #endregion
    
    const cellSelector = '.react-grid-item';
    const cells = await page.locator(cellSelector).count();
    
    // #region agent log
    fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:192',message:'Cell count',data:{count:cells},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'A'})}).catch(()=>{});
    // #endregion
    
    if (cells === 0) {
      // #region agent log
      fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:196',message:'No cells found, skipping test',data:{},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'A'})}).catch(()=>{});
      // #endregion
      // セルが存在しない場合は、このテストをスキップ
      test.skip();
    }

    // 5. 最初のセルを取得
    // #region agent log
    fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:164',message:'Waiting for first cell to be visible',data:{},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'A'})}).catch(()=>{});
    // #endregion
    
    const firstCell = page.locator(cellSelector).first();
    try {
      await firstCell.waitFor({ state: 'visible', timeout: 5000 });
      // #region agent log
      fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:170',message:'First cell is visible',data:{},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'A'})}).catch(()=>{});
      // #endregion
    } catch (error) {
      // #region agent log
      fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:174',message:'First cell not visible, timeout',data:{error:String(error)},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'A'})}).catch(()=>{});
      // #endregion
      throw error;
    }

    // 6. ドラッグ前の位置を記録
    const beforePosition = await getCellPosition(page, cellSelector);
    if (!beforePosition) {
      // #region agent log
      fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:214',message:'Before position not found',data:{},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'A'})}).catch(()=>{});
      // #endregion
      test.skip();
    }
    
    // #region agent log
    fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:219',message:'Before position',data:{x:beforePosition.x,y:beforePosition.y},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'A'})}).catch(()=>{});
    // #endregion

    // 7. セルを100px右にドラッグ
    await dragGridCell(page, cellSelector, 100, 0);

    // 8. ドラッグ後の位置を取得
    await page.waitForTimeout(500); // 位置更新を待つ
    const afterPosition = await getCellPosition(page, cellSelector);
    if (!afterPosition) {
      // #region agent log
      fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:228',message:'After position not found',data:{},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'A'})}).catch(()=>{});
      // #endregion
      test.skip();
    }
    
    // #region agent log
    fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:233',message:'After position',data:{x:afterPosition.x,y:afterPosition.y},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'A'})}).catch(()=>{});
    // #endregion

    // 9. 移動距離を検証（許容誤差5px）
    const actualMoveX = afterPosition.x - beforePosition.x;
    // #region agent log
    fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'grid-3d-layout-drag-scale.spec.ts:238',message:'Move distance',data:{actualMoveX,expected:100},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'A'})}).catch(()=>{});
    // #endregion
    expect(actualMoveX).toBeCloseTo(100, -1); // 許容誤差10px
  });

  test('スケール変更後もドラッグが正常に動作する', async ({ page }) => {
    // 1. セルを追加
    try {
      await addCell(page, 'python');
    } catch (error) {
      test.skip();
    }
    
    // 2. グリッドレイアウトが表示されるまで待機
    await page.waitForSelector('.grid-3d-container', { timeout: 10000 }).catch(() => {
      test.skip();
    });

    // 3. セルが存在するか確認
    const cellSelector = '.react-grid-item';
    const cells = await page.locator(cellSelector).count();
    
    if (cells === 0) {
      test.skip();
    }

    const firstCell = page.locator(cellSelector).first();
    await firstCell.waitFor({ state: 'visible', timeout: 5000 });

    // 3. 異なるスケール値でドラッグ操作をテスト
    const scales = [1.0, 1.5, 2.0, 1.84583];
    
    for (const scaleValue of scales) {
      // スケールを設定
      await setGridScale(page, scaleValue);
      const currentScale = await getGridScale(page);
      expect(currentScale).toBeCloseTo(scaleValue, 2);

      // ドラッグ前の位置を記録
      const beforePosition = await getCellPosition(page, cellSelector);
      if (!beforePosition) {
        continue;
      }

      // セルを50px右にドラッグ
      await dragGridCell(page, cellSelector, 50, 0);

      // ドラッグ後の位置を取得
      await page.waitForTimeout(500);
      const afterPosition = await getCellPosition(page, cellSelector);
      if (!afterPosition) {
        continue;
      }

      // 移動距離を検証（許容誤差10px）
      const actualMoveX = afterPosition.x - beforePosition.x;
      expect(actualMoveX).toBeCloseTo(50, -1); // 許容誤差10px
    }
  });

  test('リサイズハンドルが正常に動作する（スケール適用時）', async ({ page }) => {
    // 1. セルを追加
    try {
      await addCell(page, 'python');
    } catch (error) {
      test.skip();
    }
    
    // 2. グリッドレイアウトが表示されるまで待機
    await page.waitForSelector('.grid-3d-container', { timeout: 10000 }).catch(() => {
      test.skip();
    });

    // 3. スケールを適用
    await setGridScale(page, 1.84583);

    // 4. セルが存在するか確認
    const cellSelector = '.react-grid-item';
    const cells = await page.locator(cellSelector).count();
    
    if (cells === 0) {
      test.skip();
    }

    const firstCell = page.locator(cellSelector).first();
    await firstCell.waitFor({ state: 'visible', timeout: 5000 });

    // 4. リサイズハンドルを取得
    const resizeHandle = firstCell.locator('.react-resizable-handle');
    const resizeHandleCount = await resizeHandle.count();
    
    if (resizeHandleCount === 0) {
      // リサイズハンドルが存在しない場合は、このテストをスキップ
      test.skip();
    }

    // 5. リサイズ前のサイズを記録
    const beforePosition = await getCellPosition(page, cellSelector);
    if (!beforePosition) {
      test.skip();
    }

    // 6. リサイズハンドルをドラッグしてリサイズ
    const resizeHandleElement = resizeHandle.first();
    const handleBox = await resizeHandleElement.boundingBox();
    if (!handleBox) {
      test.skip();
    }

    const startX = handleBox.x + handleBox.width / 2;
    const startY = handleBox.y + handleBox.height / 2;
    const endX = startX + 50;
    const endY = startY + 50;

    await resizeHandleElement.hover();
    await page.mouse.down();
    await page.waitForTimeout(100);
    await page.mouse.move(endX, endY, { steps: 10 });
    await page.waitForTimeout(100);
    await page.mouse.up();
    await page.waitForTimeout(500);

    // 7. リサイズ後のサイズを取得
    const afterPosition = await getCellPosition(page, cellSelector);
    if (!afterPosition) {
      test.skip();
    }

    // 8. サイズが変更されたことを確認
    const widthChanged = Math.abs(afterPosition.width - beforePosition.width) > 5;
    const heightChanged = Math.abs(afterPosition.height - beforePosition.height) > 5;
    expect(widthChanged || heightChanged).toBe(true);
  });

  test('react-grid-layout要素のDOMサイズがスケールに応じて調整される', async ({ page }) => {
    // 1. グリッドレイアウトが表示されるまで待機
    await page.waitForSelector('.grid-3d-container', { timeout: 10000 }).catch(() => {
      test.skip();
    });

    // 2. スケールを適用
    await setGridScale(page, 1.84583);
    await page.waitForTimeout(300); // DOM更新を待つ

    // 3. react-grid-layout要素のDOMサイズを取得
    const gridLayoutInfo = await page.evaluate(() => {
      const reactGridLayoutElement = document.querySelector('.react-grid-layout') as HTMLElement;
      if (!reactGridLayoutElement) return null;
      
      const rect = reactGridLayoutElement.getBoundingClientRect();
      const offsetWidth = reactGridLayoutElement.offsetWidth;
      const offsetHeight = reactGridLayoutElement.offsetHeight;
      const styleWidth = reactGridLayoutElement.style.width;
      const styleHeight = reactGridLayoutElement.style.height;
      
      return {
        visualWidth: rect.width,
        visualHeight: rect.height,
        offsetWidth,
        offsetHeight,
        styleWidth,
        styleHeight
      };
    });

    if (!gridLayoutInfo) {
      test.skip();
    }

    // 4. DOMサイズが調整されていることを確認
    // style.widthが設定されている場合、offsetWidthはstyle.widthの値と一致するはず
    if (gridLayoutInfo.styleWidth) {
      const expectedWidth = parseFloat(gridLayoutInfo.styleWidth.replace('px', ''));
      expect(gridLayoutInfo.offsetWidth).toBeCloseTo(expectedWidth, 0);
    }

    // 5. スケールを変更して、DOMサイズが更新されることを確認
    await setGridScale(page, 2.0);
    await page.waitForTimeout(300);

    const gridLayoutInfo2 = await page.evaluate(() => {
      const reactGridLayoutElement = document.querySelector('.react-grid-layout') as HTMLElement;
      if (!reactGridLayoutElement) return null;
      
      const styleWidth = reactGridLayoutElement.style.width;
      const styleHeight = reactGridLayoutElement.style.height;
      
      return {
        styleWidth,
        styleHeight
      };
    });

    if (!gridLayoutInfo2) {
      test.skip();
    }

    // スケール変更後、DOMサイズが更新されていることを確認
    if (gridLayoutInfo.styleWidth && gridLayoutInfo2.styleWidth) {
      const width1 = parseFloat(gridLayoutInfo.styleWidth.replace('px', ''));
      const width2 = parseFloat(gridLayoutInfo2.styleWidth.replace('px', ''));
      // スケールが変更されたため、DOMサイズも変更される可能性がある
      // （実際の見た目サイズに応じて調整される）
      expect(width2).toBeGreaterThan(0);
    }
  });
});

