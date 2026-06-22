# -*- coding: utf-8 -*-

# Copyright (c) 2022 Tachibana Securities Co., Ltd. All rights reserved.
# 2022/03/28, yo.

# mfds_json_api_compress_v4r2.js をpython用に作成
# v4r2専用。
# mfds_json_api_compress_v4r2.jsのソースコードから
# 項目名を取り出し、1から昇順に付番しただけ。

# if文形式
# def func_name2code( key_name ):
# def func_code2name( key_code ):

# 辞書形式
# def func_make_dic_name_to_code():
# def func_make_dic_code_to_name():


# if文形式 ------------------------------------------
# 返値： 項目番号
# 引数1： 項目名
def func_name2code( key_name ):
    if key_name == 'CLMAuthCheckSecondPassword' :    key_code = '1'
    elif key_name == 'CLMAuthLoginAck' :    key_code = '2'
    elif key_name == 'CLMAuthLoginRequest' :    key_code = '3'
    elif key_name == 'CLMAuthLogoutAck' :    key_code = '4'
    elif key_name == 'CLMAuthLogoutRequest' :    key_code = '5'
    elif key_name == 'CLMAuthSessionInfo' :    key_code = '6'
    elif key_name == 'CLMAuthStkLoginRequest' :    key_code = '7'
    elif key_name == 'CLMDaiyouKakeme' :    key_code = '8'
    elif key_name == 'CLMDateZyouhou' :    key_code = '9'
    elif key_name == 'CLMEventDownload' :    key_code = '10'
    elif key_name == 'CLMEventDownloadComplete' :    key_code = '11'
    elif key_name == 'CLMGenbutuKabuList' :    key_code = '12'
    elif key_name == 'CLMHosyoukinMst' :    key_code = '13'
    elif key_name == 'CLMIssueMstKabu' :    key_code = '14'
    elif key_name == 'CLMIssueMstOp' :    key_code = '15'
    elif key_name == 'CLMIssueMstSak' :    key_code = '16'
    elif key_name == 'CLMIssueSizyouKiseiHasei' :    key_code = '17'
    elif key_name == 'CLMIssueSizyouKiseiKabu' :    key_code = '18'
    elif key_name == 'CLMIssueSizyouMstKabu' :    key_code = '19'
    elif key_name == 'CLMKabuCancelOrder' :    key_code = '20'
    elif key_name == 'CLMKabuCorrectOrder' :    key_code = '21'
    elif key_name == 'CLMKabuNewOrder' :    key_code = '22'
    elif key_name == 'CLMKabuOrderUpdateEvent' :    key_code = '23'
    elif key_name == 'CLMKeepAliveRequest' :    key_code = '24'
    elif key_name == 'CLMMfdsEventData' :    key_code = '25'
    elif key_name == 'CLMMfdsGetEventData' :    key_code = '26'
    elif key_name == 'CLMMfdsGetEventNoLast' :    key_code = '27'
    elif key_name == 'CLMMfdsGetMasterData' :    key_code = '28'
    elif key_name == 'CLMMsgTable' :    key_code = '29'
    elif key_name == 'CLMOrderErrReason' :    key_code = '30'
    elif key_name == 'CLMOrderList' :    key_code = '31'
    elif key_name == 'CLMOrderListDetail' :    key_code = '32'
    elif key_name == 'CLMShinyouTategyokuList' :    key_code = '33'
    elif key_name == 'CLMSystemStatus' :    key_code = '34'
    elif key_name == 'CLMUnyouStatus' :    key_code = '35'
    elif key_name == 'CLMUnyouStatusHasei' :    key_code = '36'
    elif key_name == 'CLMUnyouStatusKabu' :    key_code = '37'
    elif key_name == 'CLMYobine' :    key_code = '38'
    elif key_name == 'CLMZanKaiGenbutuKaitukeSyousai' :    key_code = '39'
    elif key_name == 'CLMZanKaiKanougaku' :    key_code = '40'
    elif key_name == 'CLMZanKaiKanougakuSuii' :    key_code = '41'
    elif key_name == 'CLMZanKaiSinyouSinkidateSyousai' :    key_code = '42'
    elif key_name == 'CLMZanKaiSummary' :    key_code = '43'
    elif key_name == 'CLMZanRealHosyoukinRitu' :    key_code = '44'
    elif key_name == 'CLMZanShinkiKanoIjiritu' :    key_code = '45'
    elif key_name == 'CLMZanUriKanousuu' :    key_code = '46'
    elif key_name == 'aCLMKabuHensaiData' :    key_code = '47'
    elif key_name == 'aCLMMfdsMarketPrice' :    key_code = '48'
    elif key_name == 'aGenbutuKabuList' :    key_code = '49'
    elif key_name == 'aHikazeiKouzaList' :    key_code = '50'
    elif key_name == 'aHosyoukinSeikyuZyoukyouList' :    key_code = '51'
    elif key_name == 'aKanougakuSuiiList' :    key_code = '52'
    elif key_name == 'aKessaiOrderTategyokuList' :    key_code = '53'
    elif key_name == 'aOisyouHasseiZyoukyouList' :    key_code = '54'
    elif key_name == 'aOrderList' :    key_code = '55'
    elif key_name == 'aShinyouTategyokuList' :    key_code = '56'
    elif key_name == 'aYakuzyouSikkouList' :    key_code = '57'
    elif key_name == 'pAAV' :    key_code = '58'
    elif key_name == 'pABV' :    key_code = '59'
    elif key_name == 'pAV' :    key_code = '60'
    elif key_name == 'pBICD' :    key_code = '61'
    elif key_name == 'pBV' :    key_code = '62'
    elif key_name == 'pDHF' :    key_code = '63'
    elif key_name == 'pDHP' :    key_code = '64'
    elif key_name == 'pDJ' :    key_code = '65'
    elif key_name == 'pDLF' :    key_code = '66'
    elif key_name == 'pDLP' :    key_code = '67'
    elif key_name == 'pDOP' :    key_code = '68'
    elif key_name == 'pDPG' :    key_code = '69'
    elif key_name == 'pDPP' :    key_code = '70'
    elif key_name == 'pDV' :    key_code = '71'
    elif key_name == 'pDYRP' :    key_code = '72'
    elif key_name == 'pDYWP' :    key_code = '73'
    elif key_name == 'pGAP1' :    key_code = '74'
    elif key_name == 'pGAP10' :    key_code = '75'
    elif key_name == 'pGAP2' :    key_code = '76'
    elif key_name == 'pGAP3' :    key_code = '77'
    elif key_name == 'pGAP4' :    key_code = '78'
    elif key_name == 'pGAP5' :    key_code = '79'
    elif key_name == 'pGAP6' :    key_code = '80'
    elif key_name == 'pGAP7' :    key_code = '81'
    elif key_name == 'pGAP8' :    key_code = '82'
    elif key_name == 'pGAP9' :    key_code = '83'
    elif key_name == 'pGAV1' :    key_code = '84'
    elif key_name == 'pGAV10' :    key_code = '85'
    elif key_name == 'pGAV2' :    key_code = '86'
    elif key_name == 'pGAV3' :    key_code = '87'
    elif key_name == 'pGAV4' :    key_code = '88'
    elif key_name == 'pGAV5' :    key_code = '89'
    elif key_name == 'pGAV6' :    key_code = '90'
    elif key_name == 'pGAV7' :    key_code = '91'
    elif key_name == 'pGAV8' :    key_code = '92'
    elif key_name == 'pGAV9' :    key_code = '93'
    elif key_name == 'pGBP1' :    key_code = '94'
    elif key_name == 'pGBP10' :    key_code = '95'
    elif key_name == 'pGBP2' :    key_code = '96'
    elif key_name == 'pGBP3' :    key_code = '97'
    elif key_name == 'pGBP4' :    key_code = '98'
    elif key_name == 'pGBP5' :    key_code = '99'
    elif key_name == 'pGBP6' :    key_code = '100'
    elif key_name == 'pGBP7' :    key_code = '101'
    elif key_name == 'pGBP8' :    key_code = '102'
    elif key_name == 'pGBP9' :    key_code = '103'
    elif key_name == 'pGBV1' :    key_code = '104'
    elif key_name == 'pGBV10' :    key_code = '105'
    elif key_name == 'pGBV2' :    key_code = '106'
    elif key_name == 'pGBV3' :    key_code = '107'
    elif key_name == 'pGBV4' :    key_code = '108'
    elif key_name == 'pGBV5' :    key_code = '109'
    elif key_name == 'pGBV6' :    key_code = '110'
    elif key_name == 'pGBV7' :    key_code = '111'
    elif key_name == 'pGBV8' :    key_code = '112'
    elif key_name == 'pGBV9' :    key_code = '113'
    elif key_name == 'pISCD' :    key_code = '114'
    elif key_name == 'pPRP' :    key_code = '115'
    elif key_name == 'pQAP' :    key_code = '116'
    elif key_name == 'pQAS' :    key_code = '117'
    elif key_name == 'pQBP' :    key_code = '118'
    elif key_name == 'pQBS' :    key_code = '119'
    elif key_name == 'pQOV' :    key_code = '120'
    elif key_name == 'pQUV' :    key_code = '121'
    elif key_name == 'pVWAP' :    key_code = '122'
    elif key_name == 'p_ALT' :    key_code = '123'
    elif key_name == 'p_BBKB' :    key_code = '124'
    elif key_name == 'p_CREPSR' :    key_code = '125'
    elif key_name == 'p_CREXSR' :    key_code = '126'
    elif key_name == 'p_CRPR' :    key_code = '127'
    elif key_name == 'p_CRPRKB' :    key_code = '128'
    elif key_name == 'p_CRSJ' :    key_code = '129'
    elif key_name == 'p_CRSR' :    key_code = '130'
    elif key_name == 'p_CRTKSR' :    key_code = '131'
    elif key_name == 'p_CT' :    key_code = '132'
    elif key_name == 'p_ED' :    key_code = '133'
    elif key_name == 'p_EDK' :    key_code = '134'
    elif key_name == 'p_ENO' :    key_code = '135'
    elif key_name == 'p_EPRC' :    key_code = '136'
    elif key_name == 'p_EXDT' :    key_code = '137'
    elif key_name == 'p_EXPR' :    key_code = '138'
    elif key_name == 'p_EXRC' :    key_code = '139'
    elif key_name == 'p_EXSR' :    key_code = '140'
    elif key_name == 'p_EXST' :    key_code = '141'
    elif key_name == 'p_GSCD' :    key_code = '142'
    elif key_name == 'p_IC' :    key_code = '143'
    elif key_name == 'p_IN' :    key_code = '144'
    elif key_name == 'p_KOFG' :    key_code = '145'
    elif key_name == 'p_LK' :    key_code = '146'
    elif key_name == 'p_LMIT' :    key_code = '147'
    elif key_name == 'p_MC' :    key_code = '148'
    elif key_name == 'p_NT' :    key_code = '149'
    elif key_name == 'p_ODST' :    key_code = '150'
    elif key_name == 'p_ON' :    key_code = '151'
    elif key_name == 'p_OON' :    key_code = '152'
    elif key_name == 'p_OT' :    key_code = '153'
    elif key_name == 'p_PV' :    key_code = '154'
    elif key_name == 'p_SHSB' :    key_code = '155'
    elif key_name == 'p_SS' :    key_code = '156'
    elif key_name == 'p_ST' :    key_code = '157'
    elif key_name == 'p_THKB' :    key_code = '158'
    elif key_name == 'p_TTST' :    key_code = '159'
    elif key_name == 'p_UC' :    key_code = '160'
    elif key_name == 'p_UPEXSR' :    key_code = '161'
    elif key_name == 'p_UPGKCDPR' :    key_code = '162'
    elif key_name == 'p_UPGKPR' :    key_code = '163'
    elif key_name == 'p_UPGKPRKB' :    key_code = '164'
    elif key_name == 'p_UPLMIT' :    key_code = '165'
    elif key_name == 'p_UPPR' :    key_code = '166'
    elif key_name == 'p_UPPRKB' :    key_code = '167'
    elif key_name == 'p_UPSJ' :    key_code = '168'
    elif key_name == 'p_UPSR' :    key_code = '169'
    elif key_name == 'p_US' :    key_code = '170'
    elif key_name == 'p_UU' :    key_code = '171'
    elif key_name == 'p_cmd' :    key_code = '172'
    elif key_name == 'p_err' :    key_code = '173'
    elif key_name == 'p_errno' :    key_code = '174'
    elif key_name == 'p_no' :    key_code = '175'
    elif key_name == 'p_rv_date' :    key_code = '176'
    elif key_name == 'p_sd_date' :    key_code = '177'
    elif key_name == 'sATMFlag' :    key_code = '178'
    elif key_name == 'sAzukariKin' :    key_code = '179'
    elif key_name == 'sBaDenpyouOutputUmuC' :    key_code = '180'
    elif key_name == 'sBadenpyouOutputYNC' :    key_code = '181'
    elif key_name == 'sBaiBaiDaikin' :    key_code = '182'
    elif key_name == 'sBaiBaiTesuryo' :    key_code = '183'
    elif key_name == 'sBaibaiKubun' :    key_code = '184'
    elif key_name == 'sBaibaiTani' :    key_code = '185'
    elif key_name == 'sBaibaiTaniYoku' :    key_code = '186'
    elif key_name == 'sBaibaiTeisiC' :    key_code = '187'
    elif key_name == 'sBensaiKubun' :    key_code = '188'
    elif key_name == 'sBirthDay' :    key_code = '189'
    elif key_name == 'sBondUkewatasiDay' :    key_code = '190'
    elif key_name == 'sButenCode' :    key_code = '191'
    elif key_name == 'sCLMID' :    key_code = '192'
    elif key_name == 'sChannel' :    key_code = '193'
    elif key_name == 'sCheckOnly' :    key_code = '194'
    elif key_name == 'sCondition' :    key_code = '195'
    elif key_name == 'sConditionTeiseiFlg' :    key_code = '196'
    elif key_name == 'sCreateDate' :    key_code = '197'
    elif key_name == 'sCreateTime' :    key_code = '198'
    elif key_name == 'sDaiyoHosyokinRitu' :    key_code = '199'
    elif key_name == 'sDaiyouHyoukaTanka' :    key_code = '200'
    elif key_name == 'sDaiyouHyoukagaku' :    key_code = '201'
    elif key_name == 'sDateTime' :    key_code = '202'
    elif key_name == 'sDayKey' :    key_code = '203'
    elif key_name == 'sDecimal_1' :    key_code = '204'
    elif key_name == 'sDecimal_10' :    key_code = '205'
    elif key_name == 'sDecimal_11' :    key_code = '206'
    elif key_name == 'sDecimal_12' :    key_code = '207'
    elif key_name == 'sDecimal_13' :    key_code = '208'
    elif key_name == 'sDecimal_14' :    key_code = '209'
    elif key_name == 'sDecimal_15' :    key_code = '210'
    elif key_name == 'sDecimal_16' :    key_code = '211'
    elif key_name == 'sDecimal_17' :    key_code = '212'
    elif key_name == 'sDecimal_18' :    key_code = '213'
    elif key_name == 'sDecimal_19' :    key_code = '214'
    elif key_name == 'sDecimal_2' :    key_code = '215'
    elif key_name == 'sDecimal_20' :    key_code = '216'
    elif key_name == 'sDecimal_3' :    key_code = '217'
    elif key_name == 'sDecimal_4' :    key_code = '218'
    elif key_name == 'sDecimal_5' :    key_code = '219'
    elif key_name == 'sDecimal_6' :    key_code = '220'
    elif key_name == 'sDecimal_7' :    key_code = '221'
    elif key_name == 'sDecimal_8' :    key_code = '222'
    elif key_name == 'sDecimal_9' :    key_code = '223'
    elif key_name == 'sDeleteDay' :    key_code = '224'
    elif key_name == 'sDeleteFlag' :    key_code = '225'
    elif key_name == 'sDeleteTime' :    key_code = '226'
    elif key_name == 'sEigyouDay' :    key_code = '227'
    elif key_name == 'sEigyouDayC' :    key_code = '228'
    elif key_name == 'sErrReasonCode' :    key_code = '229'
    elif key_name == 'sErrReasonText' :    key_code = '230'
    elif key_name == 'sEventName' :    key_code = '231'
    elif key_name == 'sFurikaeKouzaKubun' :    key_code = '232'
    elif key_name == 'sFusokugaku' :    key_code = '233'
    elif key_name == 'sGaikokuKouzaKubun' :    key_code = '234'
    elif key_name == 'sGaisanDaikin' :    key_code = '235'
    elif key_name == 'sGenbikiKanougaku' :    key_code = '236'
    elif key_name == 'sGenbikiWatasiHosyoukin' :    key_code = '237'
    elif key_name == 'sGenbikiWatasiTateDaikin' :    key_code = '238'
    elif key_name == 'sGenbutuBaibaiDaikin' :    key_code = '239'
    elif key_name == 'sGenbutuKabuKaituke' :    key_code = '240'
    elif key_name == 'sGenbutuKaituke' :    key_code = '241'
    elif key_name == 'sGenbutuKaitukeKanougaku' :    key_code = '242'
    elif key_name == 'sGenbutuKaitukeYoku' :    key_code = '243'
    elif key_name == 'sGenbutuOrderCount' :    key_code = '244'
    elif key_name == 'sGenbutuUrituke' :    key_code = '245'
    elif key_name == 'sGenbutuUritukeYoku' :    key_code = '246'
    elif key_name == 'sGenbutuYakuzyouCount' :    key_code = '247'
    elif key_name == 'sGenbutuZyoutoekiKazeiC' :    key_code = '248'
    elif key_name == 'sGenbwGenkinHosyoukin' :    key_code = '249'
    elif key_name == 'sGengetu' :    key_code = '250'
    elif key_name == 'sGenkinHosyokinRitu' :    key_code = '251'
    elif key_name == 'sGenkinHosyoukin' :    key_code = '252'
    elif key_name == 'sGenkinHosyoukinYoryoku' :    key_code = '253'
    elif key_name == 'sGenkinShinyouKubun' :    key_code = '254'
    elif key_name == 'sGenkinSinyouKubun' :    key_code = '255'
    elif key_name == 'sGensisanCode' :    key_code = '256'
    elif key_name == 'sGensisanKubun' :    key_code = '257'
    elif key_name == 'sGyakusasiKubun' :    key_code = '258'
    elif key_name == 'sGyakusasiOrderType' :    key_code = '259'
    elif key_name == 'sGyakusasiPrice' :    key_code = '260'
    elif key_name == 'sGyakusasiPriceKubun' :    key_code = '261'
    elif key_name == 'sGyakusasiPriceTeiseiFlg' :    key_code = '262'
    elif key_name == 'sGyakusasiZyouken' :    key_code = '263'
    elif key_name == 'sGyakusasiZyoukenTeiseiFlg' :    key_code = '264'
    elif key_name == 'sGyoumuZyoutai' :    key_code = '265'
    elif key_name == 'sGyousyuCode' :    key_code = '266'
    elif key_name == 'sGyousyuName' :    key_code = '267'
    elif key_name == 'sHakkouKaisiDay' :    key_code = '268'
    elif key_name == 'sHakkouSaisyuDay' :    key_code = '269'
    elif key_name == 'sHattyuDaiyouHyoukagaku' :    key_code = '270'
    elif key_name == 'sHattyuGenkinHosyoukin' :    key_code = '271'
    elif key_name == 'sHattyuHosyoukin' :    key_code = '272'
    elif key_name == 'sHattyuTateDaikin' :    key_code = '273'
    elif key_name == 'sHattyuZyutoukin' :    key_code = '274'
    elif key_name == 'sHenkouDay' :    key_code = '275'
    elif key_name == 'sHibakariKousokukin' :    key_code = '276'
    elif key_name == 'sHibuGyakuhibuKousokukin' :    key_code = '277'
    elif key_name == 'sHibuGyakuhibuSyueki' :    key_code = '278'
    elif key_name == 'sHikazeiC' :    key_code = '279'
    elif key_name == 'sHikazeiKouzaKaituke' :    key_code = '280'
    elif key_name == 'sHikazeiKouzaKubun' :    key_code = '281'
    elif key_name == 'sHikazeiTekiyouYear' :    key_code = '282'
    elif key_name == 'sHituke' :    key_code = '283'
    elif key_name == 'sHitukeIndex' :    key_code = '284'
    elif key_name == 'sHituyouGenkinHosyoukin' :    key_code = '285'
    elif key_name == 'sHituyouHosyoukin' :    key_code = '286'
    elif key_name == 'sHogoAdukariKouzaKubun' :    key_code = '287'
    elif key_name == 'sHosyokinDaiyoKakeme' :    key_code = '288'
    elif key_name == 'sHosyouKinritu' :    key_code = '289'
    elif key_name == 'sHosyoukin' :    key_code = '290'
    elif key_name == 'sHosyoukinDaiyouKakeme' :    key_code = '291'
    elif key_name == 'sHosyoukinGenbutuKaitukeKanougaku' :    key_code = '292'
    elif key_name == 'sHosyoukinHikidasiKousokukin' :    key_code = '293'
    elif key_name == 'sHosyoukinHikidasiYoryoku' :    key_code = '294'
    elif key_name == 'sHosyoukinIziRitu' :    key_code = '295'
    elif key_name == 'sHosyoukinIzirituIziYoryoku' :    key_code = '296'
    elif key_name == 'sHosyoukinRitu' :    key_code = '297'
    elif key_name == 'sHosyoukinRituIziYoryoku' :    key_code = '298'
    elif key_name == 'sHosyoukinTyousyuRitu' :    key_code = '299'
    elif key_name == 'sHosyoukinYoryoku' :    key_code = '300'
    elif key_name == 'sHshzGenkinHosyoukin' :    key_code = '301'
    elif key_name == 'sHshzGenkinHosyoukinHasseiDay' :    key_code = '302'
    elif key_name == 'sHshzHosyoukin' :    key_code = '303'
    elif key_name == 'sHshzHosyoukinHasseiDay' :    key_code = '304'
    elif key_name == 'sHshzNyukinKigenDay' :    key_code = '305'
    elif key_name == 'sHusokukinHasseiFlg' :    key_code = '306'
    elif key_name == 'sHyoukaSonEki' :    key_code = '307'
    elif key_name == 'sHyoukaSonekiGoukeiKaidate' :    key_code = '308'
    elif key_name == 'sHyoukaSonekiGoukeiUridate' :    key_code = '309'
    elif key_name == 'sIPOKounyu' :    key_code = '310'
    elif key_name == 'sInsiderAgreement' :    key_code = '311'
    elif key_name == 'sIppanGaisanHyoukaSonekiGoukei' :    key_code = '312'
    elif key_name == 'sIppanGaisanHyoukagakuGoukei' :    key_code = '313'
    elif key_name == 'sIppanHyoukaSonekiGoukei' :    key_code = '314'
    elif key_name == 'sIppanSinyouGenbiki' :    key_code = '315'
    elif key_name == 'sIppanSinyouGenbikiYoku' :    key_code = '316'
    elif key_name == 'sIppanSinyouGenwatasi' :    key_code = '317'
    elif key_name == 'sIppanSinyouGenwatasiYoku' :    key_code = '318'
    elif key_name == 'sIppanSinyouKaiHensai' :    key_code = '319'
    elif key_name == 'sIppanSinyouKaiHensaiYoku' :    key_code = '320'
    elif key_name == 'sIppanSinyouSinkiKaitate' :    key_code = '321'
    elif key_name == 'sIppanSinyouSinkiKaitateYoku' :    key_code = '322'
    elif key_name == 'sIppanSinyouSinkiUritate' :    key_code = '323'
    elif key_name == 'sIppanSinyouSinkiUritateYoku' :    key_code = '324'
    elif key_name == 'sIppanSinyouUriHensai' :    key_code = '325'
    elif key_name == 'sIppanSinyouUriHensaiYoku' :    key_code = '326'
    elif key_name == 'sIssueBubetuC' :    key_code = '327'
    elif key_name == 'sIssueCode' :    key_code = '328'
    elif key_name == 'sIssueKisei1C' :    key_code = '329'
    elif key_name == 'sIssueKisei2C' :    key_code = '330'
    elif key_name == 'sIssueKubunC' :    key_code = '331'
    elif key_name == 'sIssueName' :    key_code = '332'
    elif key_name == 'sIssueNameEizi' :    key_code = '333'
    elif key_name == 'sIssueNameKana' :    key_code = '334'
    elif key_name == 'sIssueNameRyaku' :    key_code = '335'
    elif key_name == 'sItakuHosyoukinRitu' :    key_code = '336'
    elif key_name == 'sItakuhosyoukin' :    key_code = '337'
    elif key_name == 'sJsonOfmt' :    key_code = '338'
    elif key_name == 'sKabuKariUkewatasiDay' :    key_code = '339'
    elif key_name == 'sKabuUkewatasiDay' :    key_code = '340'
    elif key_name == 'sKaiHensai' :    key_code = '341'
    elif key_name == 'sKaiHensaiYoku' :    key_code = '342'
    elif key_name == 'sKaitate' :    key_code = '343'
    elif key_name == 'sKaitateDaikin' :    key_code = '344'
    elif key_name == 'sKaitateYoku' :    key_code = '345'
    elif key_name == 'sKarauriHugou' :    key_code = '346'
    elif key_name == 'sKarikessaiC' :    key_code = '347'
    elif key_name == 'sKawaseKouzaKubun' :    key_code = '348'
    elif key_name == 'sKenrikousiLastDay' :    key_code = '349'
    elif key_name == 'sKenriotiFlag' :    key_code = '350'
    elif key_name == 'sKenritukiSaisyuDay' :    key_code = '351'
    elif key_name == 'sKessaiGyakuhibu' :    key_code = '352'
    elif key_name == 'sKessaiKakikaeryou' :    key_code = '353'
    elif key_name == 'sKessaiKanrihi' :    key_code = '354'
    elif key_name == 'sKessaiKasikaburyou' :    key_code = '355'
    elif key_name == 'sKessaiOrderSuryo' :    key_code = '356'
    elif key_name == 'sKessaiSoneki' :    key_code = '357'
    elif key_name == 'sKessaiSonota' :    key_code = '358'
    elif key_name == 'sKessaiTateTesuryou' :    key_code = '359'
    elif key_name == 'sKessaiTatebiZyuni' :    key_code = '360'
    elif key_name == 'sKessaiTategyokuDay' :    key_code = '361'
    elif key_name == 'sKessaiTategyokuPrice' :    key_code = '362'
    elif key_name == 'sKessaiTotalSiteibi' :    key_code = '363'
    elif key_name == 'sKessaiTotalToday' :    key_code = '364'
    elif key_name == 'sKessaiWarningCode' :    key_code = '365'
    elif key_name == 'sKessaiWarningText' :    key_code = '366'
    elif key_name == 'sKessaiYakuzyouPrice' :    key_code = '367'
    elif key_name == 'sKessaiYakuzyouSuryo' :    key_code = '368'
    elif key_name == 'sKessaiZyunHibu' :    key_code = '369'
    elif key_name == 'sKessanC' :    key_code = '370'
    elif key_name == 'sKessanDay' :    key_code = '371'
    elif key_name == 'sKikoSankaC' :    key_code = '372'
    elif key_name == 'sKinri' :    key_code = '373'
    elif key_name == 'sKinsyouhouMidokuFlg' :    key_code = '374'
    elif key_name == 'sKizunPrice_1' :    key_code = '375'
    elif key_name == 'sKizunPrice_10' :    key_code = '376'
    elif key_name == 'sKizunPrice_11' :    key_code = '377'
    elif key_name == 'sKizunPrice_12' :    key_code = '378'
    elif key_name == 'sKizunPrice_13' :    key_code = '379'
    elif key_name == 'sKizunPrice_14' :    key_code = '380'
    elif key_name == 'sKizunPrice_15' :    key_code = '381'
    elif key_name == 'sKizunPrice_16' :    key_code = '382'
    elif key_name == 'sKizunPrice_17' :    key_code = '383'
    elif key_name == 'sKizunPrice_18' :    key_code = '384'
    elif key_name == 'sKizunPrice_19' :    key_code = '385'
    elif key_name == 'sKizunPrice_2' :    key_code = '386'
    elif key_name == 'sKizunPrice_20' :    key_code = '387'
    elif key_name == 'sKizunPrice_3' :    key_code = '388'
    elif key_name == 'sKizunPrice_4' :    key_code = '389'
    elif key_name == 'sKizunPrice_5' :    key_code = '390'
    elif key_name == 'sKizunPrice_6' :    key_code = '391'
    elif key_name == 'sKizunPrice_7' :    key_code = '392'
    elif key_name == 'sKizunPrice_8' :    key_code = '393'
    elif key_name == 'sKizunPrice_9' :    key_code = '394'
    elif key_name == 'sKoKey' :    key_code = '395'
    elif key_name == 'sKouboPrice' :    key_code = '396'
    elif key_name == 'sKousiPrice' :    key_code = '397'
    elif key_name == 'sLargeKaidateYoryoku' :    key_code = '398'
    elif key_name == 'sLargeUridateYoryoku' :    key_code = '399'
    elif key_name == 'sLastBaibaiDay' :    key_code = '400'
    elif key_name == 'sLastLoginDate' :    key_code = '401'
    elif key_name == 'sLoginKyokaKubun' :    key_code = '402'
    elif key_name == 'sMMFKouzaKubun' :    key_code = '403'
    elif key_name == 'sMRFKouzaKubun' :    key_code = '404'
    elif key_name == 'sMaeEigyouDay_1' :    key_code = '405'
    elif key_name == 'sMaeEigyouDay_2' :    key_code = '406'
    elif key_name == 'sMaeEigyouDay_3' :    key_code = '407'
    elif key_name == 'sMeisaiNumber' :    key_code = '408'
    elif key_name == 'sMeyasuTime' :    key_code = '409'
    elif key_name == 'sMikessaiGenkinHosyoukin' :    key_code = '410'
    elif key_name == 'sMikessaiTateDaikin' :    key_code = '411'
    elif key_name == 'sMiniKaidateYoryoku' :    key_code = '412'
    elif key_name == 'sMiniUridateYoryoku' :    key_code = '413'
    elif key_name == 'sMiukewatasiKessaiEki' :    key_code = '414'
    elif key_name == 'sMiukewatasiKessaiSon' :    key_code = '415'
    elif key_name == 'sMsgId' :    key_code = '416'
    elif key_name == 'sMukigenC' :    key_code = '417'
    elif key_name == 'sNearaiKubun' :    key_code = '418'
    elif key_name == 'sNehabaCheckKahiC' :    key_code = '419'
    elif key_name == 'sNehabaKigenDay' :    key_code = '420'
    elif key_name == 'sNehabaKiseiC' :    key_code = '421'
    elif key_name == 'sNehabaKiseiTi' :    key_code = '422'
    elif key_name == 'sNehabaMax' :    key_code = '423'
    elif key_name == 'sNehabaMin' :    key_code = '424'
    elif key_name == 'sNehabaSansyutuSizyouC' :    key_code = '425'
    elif key_name == 'sNehabaSizyouC' :    key_code = '426'
    elif key_name == 'sNiruiKizituC' :    key_code = '427'
    elif key_name == 'sNisaGaisanHyoukaSonekiGoukei' :    key_code = '428'
    elif key_name == 'sNisaGaisanHyoukagakuGoukei' :    key_code = '429'
    elif key_name == 'sNisaKaitukeKanougaku' :    key_code = '430'
    elif key_name == 'sNissyoukinKasikabuZan' :    key_code = '431'
    elif key_name == 'sNyusatuDay' :    key_code = '432'
    elif key_name == 'sNyusatuKaizyoDay' :    key_code = '433'
    elif key_name == 'sOhzHasseiDay' :    key_code = '434'
    elif key_name == 'sOhzHosyoukinRitu' :    key_code = '435'
    elif key_name == 'sOhzHosyoukinZougen' :    key_code = '436'
    elif key_name == 'sOhzKakuteiFlg' :    key_code = '437'
    elif key_name == 'sOhzKessaisonNyukin' :    key_code = '438'
    elif key_name == 'sOhzMikaisyouKingaku' :    key_code = '439'
    elif key_name == 'sOhzMikaisyouKingakuFlg' :    key_code = '440'
    elif key_name == 'sOhzNyukin' :    key_code = '441'
    elif key_name == 'sOhzNyukinKigenDay' :    key_code = '442'
    elif key_name == 'sOhzOisyouKingaku' :    key_code = '443'
    elif key_name == 'sOhzTategyokuKessai' :    key_code = '444'
    elif key_name == 'sOhzsDaiyouHyoukagaku' :    key_code = '445'
    elif key_name == 'sOhzsGenkinHosyoukin' :    key_code = '446'
    elif key_name == 'sOhzsHyoukaSoneki' :    key_code = '447'
    elif key_name == 'sOhzsItakuHosyoukinRitu' :    key_code = '448'
    elif key_name == 'sOhzsKeisanDay' :    key_code = '449'
    elif key_name == 'sOhzsMiukeKessaiEki' :    key_code = '450'
    elif key_name == 'sOhzsMiukeKessaiSon' :    key_code = '451'
    elif key_name == 'sOhzsSasiireHosyoukin' :    key_code = '452'
    elif key_name == 'sOhzsSyokeihi' :    key_code = '453'
    elif key_name == 'sOhzsTatekabuDaikin' :    key_code = '454'
    elif key_name == 'sOhzsUkeireHosyoukin' :    key_code = '455'
    elif key_name == 'sOisyouHasseiFlg' :    key_code = '456'
    elif key_name == 'sOisyouHituyouHosyoukin' :    key_code = '457'
    elif key_name == 'sOisyouKakuteiFlg' :    key_code = '458'
    elif key_name == 'sOisyouYoryoku' :    key_code = '459'
    elif key_name == 'sOogutiKabusu' :    key_code = '460'
    elif key_name == 'sOogutiKingaku' :    key_code = '461'
    elif key_name == 'sOpBaibaiDaikin' :    key_code = '462'
    elif key_name == 'sOpKaidateYoryokyu' :    key_code = '463'
    elif key_name == 'sOpOrderCount' :    key_code = '464'
    elif key_name == 'sOpYakuzyouCount' :    key_code = '465'
    elif key_name == 'sOrderAcceptTime' :    key_code = '466'
    elif key_name == 'sOrderBaibaiKubun' :    key_code = '467'
    elif key_name == 'sOrderBensaiKubun' :    key_code = '468'
    elif key_name == 'sOrderCondition' :    key_code = '469'
    elif key_name == 'sOrderCorrectCancelKahiFlg' :    key_code = '470'
    elif key_name == 'sOrderCurrentSuryou' :    key_code = '471'
    elif key_name == 'sOrderDate' :    key_code = '472'
    elif key_name == 'sOrderExpireDay' :    key_code = '473'
    elif key_name == 'sOrderExpireDayTeiseiFlg' :    key_code = '474'
    elif key_name == 'sOrderGaisanHyoukaSoneki' :    key_code = '475'
    elif key_name == 'sOrderGaisanHyoukaSonekiRitu' :    key_code = '476'
    elif key_name == 'sOrderGenbikiGenwatasiKabusu' :    key_code = '477'
    elif key_name == 'sOrderGyakuhibu' :    key_code = '478'
    elif key_name == 'sOrderGyakusasiKubun' :    key_code = '479'
    elif key_name == 'sOrderGyakusasiOrderType' :    key_code = '480'
    elif key_name == 'sOrderGyakusasiPrice' :    key_code = '481'
    elif key_name == 'sOrderGyakusasiZyouken' :    key_code = '482'
    elif key_name == 'sOrderHensaiKanouSuryou' :    key_code = '483'
    elif key_name == 'sOrderHyoukaTanka' :    key_code = '484'
    elif key_name == 'sOrderIssueCode' :    key_code = '485'
    elif key_name == 'sOrderKakikaeryou' :    key_code = '486'
    elif key_name == 'sOrderKanrihi' :    key_code = '487'
    elif key_name == 'sOrderKasikaburyou' :    key_code = '488'
    elif key_name == 'sOrderKurikosiOrderFlg' :    key_code = '489'
    elif key_name == 'sOrderNumber' :    key_code = '490'
    elif key_name == 'sOrderOrderDateTime' :    key_code = '491'
    elif key_name == 'sOrderOrderExpireDay' :    key_code = '492'
    elif key_name == 'sOrderOrderNumber' :    key_code = '493'
    elif key_name == 'sOrderOrderPrice' :    key_code = '494'
    elif key_name == 'sOrderOrderPriceKubun' :    key_code = '495'
    elif key_name == 'sOrderOrderSuryou' :    key_code = '496'
    elif key_name == 'sOrderPrice' :    key_code = '497'
    elif key_name == 'sOrderPriceKubun' :    key_code = '498'
    elif key_name == 'sOrderPriceTeiseiFlg' :    key_code = '499'
    elif key_name == 'sOrderSikkouDay' :    key_code = '500'
    elif key_name == 'sOrderSizyouC' :    key_code = '501'
    elif key_name == 'sOrderSonota' :    key_code = '502'
    elif key_name == 'sOrderStatus' :    key_code = '503'
    elif key_name == 'sOrderStatusCode' :    key_code = '504'
    elif key_name == 'sOrderSuryou' :    key_code = '505'
    elif key_name == 'sOrderSuryouTeiseiFlg' :    key_code = '506'
    elif key_name == 'sOrderSyouhizei' :    key_code = '507'
    elif key_name == 'sOrderSyoukaiStatus' :    key_code = '508'
    elif key_name == 'sOrderTateTesuryou' :    key_code = '509'
    elif key_name == 'sOrderTatebiType' :    key_code = '510'
    elif key_name == 'sOrderTategyokuDay' :    key_code = '511'
    elif key_name == 'sOrderTategyokuKizituDay' :    key_code = '512'
    elif key_name == 'sOrderTategyokuNumber' :    key_code = '513'
    elif key_name == 'sOrderTategyokuSuryou' :    key_code = '514'
    elif key_name == 'sOrderTategyokuTanka' :    key_code = '515'
    elif key_name == 'sOrderTesuryou' :    key_code = '516'
    elif key_name == 'sOrderTriggerType' :    key_code = '517'
    elif key_name == 'sOrderType' :    key_code = '518'
    elif key_name == 'sOrderUkewatasiKingaku' :    key_code = '519'
    elif key_name == 'sOrderUtidekiKbn' :    key_code = '520'
    elif key_name == 'sOrderWarningCode' :    key_code = '521'
    elif key_name == 'sOrderWarningText' :    key_code = '522'
    elif key_name == 'sOrderYakuzyouHensaiKabusu' :    key_code = '523'
    elif key_name == 'sOrderYakuzyouPrice' :    key_code = '524'
    elif key_name == 'sOrderYakuzyouStatus' :    key_code = '525'
    elif key_name == 'sOrderYakuzyouSuryo' :    key_code = '526'
    elif key_name == 'sOrderZougen' :    key_code = '527'
    elif key_name == 'sOrderZyoutoekiKazeiC' :    key_code = '528'
    elif key_name == 'sOrderZyunHibu' :    key_code = '529'
    elif key_name == 'sOyaKey' :    key_code = '530'
    elif key_name == 'sPassword' :    key_code = '531'
    elif key_name == 'sPutCall' :    key_code = '532'
    elif key_name == 'sReason' :    key_code = '533'
    elif key_name == 'sResultCode' :    key_code = '534'
    elif key_name == 'sResultText' :    key_code = '535'
    elif key_name == 'sRuitouKaituke' :    key_code = '536'
    elif key_name == 'sSaiteiHituyouHosyoukin' :    key_code = '537'
    elif key_name == 'sSakOpSyouhin' :    key_code = '538'
    elif key_name == 'sSakiBaibaiDaikin' :    key_code = '539'
    elif key_name == 'sSakiOrderCount' :    key_code = '540'
    elif key_name == 'sSakiYakuzyouCount' :    key_code = '541'
    elif key_name == 'sSakopKouzaKubun' :    key_code = '542'
    elif key_name == 'sSasiireHosyoukin' :    key_code = '543'
    elif key_name == 'sSecondPassword' :    key_code = '544'
    elif key_name == 'sSecondPasswordOmit' :    key_code = '545'
    elif key_name == 'sSeidoSinyouGenbiki' :    key_code = '546'
    elif key_name == 'sSeidoSinyouGenbikiYoku' :    key_code = '547'
    elif key_name == 'sSeidoSinyouGenwatasi' :    key_code = '548'
    elif key_name == 'sSeidoSinyouGenwatasiYoku' :    key_code = '549'
    elif key_name == 'sSeidoSinyouKaiHensai' :    key_code = '550'
    elif key_name == 'sSeidoSinyouKaiHensaiYoku' :    key_code = '551'
    elif key_name == 'sSeidoSinyouSinkiKaitate' :    key_code = '552'
    elif key_name == 'sSeidoSinyouSinkiKaitateYoku' :    key_code = '553'
    elif key_name == 'sSeidoSinyouSinkiUritate' :    key_code = '554'
    elif key_name == 'sSeidoSinyouSinkiUritateYoku' :    key_code = '555'
    elif key_name == 'sSeidoSinyouUriHensai' :    key_code = '556'
    elif key_name == 'sSeidoSinyouUriHensaiYoku' :    key_code = '557'
    elif key_name == 'sShouhizei' :    key_code = '558'
    elif key_name == 'sSikkouDay' :    key_code = '559'
    elif key_name == 'sSinkiTesuryou' :    key_code = '560'
    elif key_name == 'sSinkiZyouzyouDay' :    key_code = '561'
    elif key_name == 'sSinyouBaibaiDaikin' :    key_code = '562'
    elif key_name == 'sSinyouC' :    key_code = '563'
    elif key_name == 'sSinyouGenbiki' :    key_code = '564'
    elif key_name == 'sSinyouKouzaKubun' :    key_code = '565'
    elif key_name == 'sSinyouOrderCount' :    key_code = '566'
    elif key_name == 'sSinyouSinkidate' :    key_code = '567'
    elif key_name == 'sSinyouSinkidateKanougaku' :    key_code = '568'
    elif key_name == 'sSinyouSyutyuKubun' :    key_code = '569'
    elif key_name == 'sSinyouSyutyuKubunYoku' :    key_code = '570'
    elif key_name == 'sSinyouTadeSyoukeihi' :    key_code = '571'
    elif key_name == 'sSinyouTateHyoukaEki' :    key_code = '572'
    elif key_name == 'sSinyouTateHyoukaSon' :    key_code = '573'
    elif key_name == 'sSinyouYakuzyouCount' :    key_code = '574'
    elif key_name == 'sSinyouZyoutoekiKazeiC' :    key_code = '575'
    elif key_name == 'sSizyouC' :    key_code = '576'
    elif key_name == 'sSizyouErrorCode' :    key_code = '577'
    elif key_name == 'sSizyoubetuBaibaiTani' :    key_code = '578'
    elif key_name == 'sSizyoubetuBaibaiTaniYoku' :    key_code = '579'
    elif key_name == 'sSogoKouzaKubun' :    key_code = '580'
    elif key_name == 'sSokuzituNyukinC' :    key_code = '581'
    elif key_name == 'sSokuzituNyukinCYoku' :    key_code = '582'
    elif key_name == 'sSokuzituNyukinKiseiDate' :    key_code = '583'
    elif key_name == 'sSonotaKousokukin' :    key_code = '584'
    elif key_name == 'sSonotaTateSyokeihi' :    key_code = '585'
    elif key_name == 'sSorC' :    key_code = '586'
    elif key_name == 'sStkSessionId' :    key_code = '587'
    elif key_name == 'sSummaryGenkabuKaituke' :    key_code = '588'
    elif key_name == 'sSummaryNisaKaitukeKanougaku' :    key_code = '589'
    elif key_name == 'sSummarySinyouSinkidate' :    key_code = '590'
    elif key_name == 'sSummaryUpdate' :    key_code = '591'
    elif key_name == 'sSyouhinType' :    key_code = '592'
    elif key_name == 'sSyoukokinFusokugaku' :    key_code = '593'
    elif key_name == 'sSystemC' :    key_code = '594'
    elif key_name == 'sSystemKouzaKubun' :    key_code = '595'
    elif key_name == 'sSystemStatus' :    key_code = '596'
    elif key_name == 'sSystemStatusKey' :    key_code = '597'
    elif key_name == 'sSyukkin' :    key_code = '598'
    elif key_name == 'sSyukkinKanougaku' :    key_code = '599'
    elif key_name == 'sSyuzituOwarine' :    key_code = '600'
    elif key_name == 'sT0HyoukaSonEki' :    key_code = '601'
    elif key_name == 'sT0ItakuHosyoukinRitu' :    key_code = '602'
    elif key_name == 'sT0OisyouHituyouHosyoukin' :    key_code = '603'
    elif key_name == 'sT0OisyouYoryoku' :    key_code = '604'
    elif key_name == 'sT0SasiireHosyoukin' :    key_code = '605'
    elif key_name == 'sT0TateKabuDaikin' :    key_code = '606'
    elif key_name == 'sT0UkeireHosyoukin' :    key_code = '607'
    elif key_name == 'sT5HyoukaSonEki' :    key_code = '608'
    elif key_name == 'sT5ItakuHosyoukinRitu' :    key_code = '609'
    elif key_name == 'sT5OisyouHituyouHosyoukin' :    key_code = '610'
    elif key_name == 'sT5OisyouYoryoku' :    key_code = '611'
    elif key_name == 'sT5SasiireHosyoukin' :    key_code = '612'
    elif key_name == 'sT5TateKabuDaikin' :    key_code = '613'
    elif key_name == 'sT5UkeireHosyoukin' :    key_code = '614'
    elif key_name == 'sTaisyouGyoumu' :    key_code = '615'
    elif key_name == 'sTaniSuryou' :    key_code = '616'
    elif key_name == 'sTargetCLMID' :    key_code = '617'
    elif key_name == 'sTargetColumn' :    key_code = '618'
    elif key_name == 'sTateKabuDaikin' :    key_code = '619'
    elif key_name == 'sTatebiType' :    key_code = '620'
    elif key_name == 'sTatebiZyuni' :    key_code = '621'
    elif key_name == 'sTategyokuDaikin' :    key_code = '622'
    elif key_name == 'sTategyokuNumber' :    key_code = '623'
    elif key_name == 'sTategyokuSuryou' :    key_code = '624'
    elif key_name == 'sTategyokuZyoutoekiKazeiC' :    key_code = '625'
    elif key_name == 'sTatekabuHyoukaSoneki' :    key_code = '626'
    elif key_name == 'sTatekabuSyoukeihi' :    key_code = '627'
    elif key_name == 'sTatekaekinHasseiFlg' :    key_code = '628'
    elif key_name == 'sTeisiKubun' :    key_code = '629'
    elif key_name == 'sTekiyouDay' :    key_code = '630'
    elif key_name == 'sText' :    key_code = '631'
    elif key_name == 'sTheDay' :    key_code = '632'
    elif key_name == 'sThzHibakariKousokukin' :    key_code = '633'
    elif key_name == 'sThzHituyouNyukingaku' :    key_code = '634'
    elif key_name == 'sThzHurikaegaku' :    key_code = '635'
    elif key_name == 'sThzKakuteiFlg' :    key_code = '636'
    elif key_name == 'sThzNyukinKigenDay' :    key_code = '637'
    elif key_name == 'sThzSeisangaku' :    key_code = '638'
    elif key_name == 'sTokuteiF' :    key_code = '639'
    elif key_name == 'sTokuteiGaisanHyoukaSonekiGoukei' :    key_code = '640'
    elif key_name == 'sTokuteiGaisanHyoukagakuGoukei' :    key_code = '641'
    elif key_name == 'sTokuteiHaitouKouzaKubun' :    key_code = '642'
    elif key_name == 'sTokuteiHyoukaSonekiGoukei' :    key_code = '643'
    elif key_name == 'sTokuteiKanriKouzaKubun' :    key_code = '644'
    elif key_name == 'sTokuteiKouzaKubunGenbutu' :    key_code = '645'
    elif key_name == 'sTokuteiKouzaKubunSinyou' :    key_code = '646'
    elif key_name == 'sTokuteiKouzaKubunTousin' :    key_code = '647'
    elif key_name == 'sTorihikiStartDay' :    key_code = '648'
    elif key_name == 'sTotalDaikin' :    key_code = '649'
    elif key_name == 'sTotalGaisanHyoukaSonekiGoukei' :    key_code = '650'
    elif key_name == 'sTotalGaisanHyoukagakuGoukei' :    key_code = '651'
    elif key_name == 'sTotalHyoukaSonekiGoukei' :    key_code = '652'
    elif key_name == 'sTousinKaituke' :    key_code = '653'
    elif key_name == 'sTousinKaitukeKanougaku' :    key_code = '654'
    elif key_name == 'sTouzituKessaiEki' :    key_code = '655'
    elif key_name == 'sTouzituKessaiSon' :    key_code = '656'
    elif key_name == 'sTouzituKessaiZenHyouka' :    key_code = '657'
    elif key_name == 'sTriggerTime' :    key_code = '658'
    elif key_name == 'sTriggerType' :    key_code = '659'
    elif key_name == 'sTyukokufKouzaKubun' :    key_code = '660'
    elif key_name == 'sUkeireHosyoukin' :    key_code = '661'
    elif key_name == 'sUkewatasiDay' :    key_code = '662'
    elif key_name == 'sUkewatasiTategyokuEki' :    key_code = '663'
    elif key_name == 'sUkewatasiTategyokuSon' :    key_code = '664'
    elif key_name == 'sUnyouCategory' :    key_code = '665'
    elif key_name == 'sUnyouStatus' :    key_code = '666'
    elif key_name == 'sUnyouUnit' :    key_code = '667'
    elif key_name == 'sUpDownFlag' :    key_code = '668'
    elif key_name == 'sUpdateDate' :    key_code = '669'
    elif key_name == 'sUpdateNumber' :    key_code = '670'
    elif key_name == 'sUpdateTime' :    key_code = '671'
    elif key_name == 'sUriHensai' :    key_code = '672'
    elif key_name == 'sUriHensaiYoku' :    key_code = '673'
    elif key_name == 'sUriOrderGaisanBokaTanka' :    key_code = '674'
    elif key_name == 'sUriOrderGaisanHyoukaSoneki' :    key_code = '675'
    elif key_name == 'sUriOrderGaisanHyoukaSonekiRitu' :    key_code = '676'
    elif key_name == 'sUriOrderGaisanHyoukagaku' :    key_code = '677'
    elif key_name == 'sUriOrderHyoukaTanka' :    key_code = '678'
    elif key_name == 'sUriOrderIssueCode' :    key_code = '679'
    elif key_name == 'sUriOrderUritukeKanouSuryou' :    key_code = '680'
    elif key_name == 'sUriOrderWarningCode' :    key_code = '681'
    elif key_name == 'sUriOrderWarningText' :    key_code = '682'
    elif key_name == 'sUriOrderZanKabuSuryou' :    key_code = '683'
    elif key_name == 'sUriOrderZyoutoekiKazeiC' :    key_code = '684'
    elif key_name == 'sUritate' :    key_code = '685'
    elif key_name == 'sUritateDaikin' :    key_code = '686'
    elif key_name == 'sUritateYoku' :    key_code = '687'
    elif key_name == 'sUrlEvent' :    key_code = '688'
    elif key_name == 'sUrlRequest' :    key_code = '689'
    elif key_name == 'sUserId' :    key_code = '690'
    elif key_name == 'sUtidekiKubun' :    key_code = '691'
    elif key_name == 'sWarningCode' :    key_code = '692'
    elif key_name == 'sWarningText' :    key_code = '693'
    elif key_name == 'sYakuzyouDate' :    key_code = '694'
    elif key_name == 'sYakuzyouPrice' :    key_code = '695'
    elif key_name == 'sYakuzyouSuryou' :    key_code = '696'
    elif key_name == 'sYakuzyouWarningCode' :    key_code = '697'
    elif key_name == 'sYakuzyouWarningText' :    key_code = '698'
    elif key_name == 'sYobineTaniNumber' :    key_code = '699'
    elif key_name == 'sYobineTaniNumberYoku' :    key_code = '700'
    elif key_name == 'sYobineTanka_1' :    key_code = '701'
    elif key_name == 'sYobineTanka_10' :    key_code = '702'
    elif key_name == 'sYobineTanka_11' :    key_code = '703'
    elif key_name == 'sYobineTanka_12' :    key_code = '704'
    elif key_name == 'sYobineTanka_13' :    key_code = '705'
    elif key_name == 'sYobineTanka_14' :    key_code = '706'
    elif key_name == 'sYobineTanka_15' :    key_code = '707'
    elif key_name == 'sYobineTanka_16' :    key_code = '708'
    elif key_name == 'sYobineTanka_17' :    key_code = '709'
    elif key_name == 'sYobineTanka_18' :    key_code = '710'
    elif key_name == 'sYobineTanka_19' :    key_code = '711'
    elif key_name == 'sYobineTanka_2' :    key_code = '712'
    elif key_name == 'sYobineTanka_20' :    key_code = '713'
    elif key_name == 'sYobineTanka_3' :    key_code = '714'
    elif key_name == 'sYobineTanka_4' :    key_code = '715'
    elif key_name == 'sYobineTanka_5' :    key_code = '716'
    elif key_name == 'sYobineTanka_6' :    key_code = '717'
    elif key_name == 'sYobineTanka_7' :    key_code = '718'
    elif key_name == 'sYobineTanka_8' :    key_code = '719'
    elif key_name == 'sYobineTanka_9' :    key_code = '720'
    elif key_name == 'sYokuEigyouDay_1' :    key_code = '721'
    elif key_name == 'sYokuEigyouDay_10' :    key_code = '722'
    elif key_name == 'sYokuEigyouDay_2' :    key_code = '723'
    elif key_name == 'sYokuEigyouDay_3' :    key_code = '724'
    elif key_name == 'sYokuEigyouDay_4' :    key_code = '725'
    elif key_name == 'sYokuEigyouDay_5' :    key_code = '726'
    elif key_name == 'sYokuEigyouDay_6' :    key_code = '727'
    elif key_name == 'sYokuEigyouDay_7' :    key_code = '728'
    elif key_name == 'sYokuEigyouDay_8' :    key_code = '729'
    elif key_name == 'sYokuEigyouDay_9' :    key_code = '730'
    elif key_name == 'sYusenSizyou' :    key_code = '731'
    elif key_name == 'sZanKabuSuryouUriKanouIppan' :    key_code = '732'
    elif key_name == 'sZanKabuSuryouUriKanouNisa' :    key_code = '733'
    elif key_name == 'sZanKabuSuryouUriKanouTokutei' :    key_code = '734'
    elif key_name == 'sZenzituHi' :    key_code = '735'
    elif key_name == 'sZenzituHiPer' :    key_code = '736'
    elif key_name == 'sZenzituOwarine' :    key_code = '737'
    elif key_name == 'sZenzituRironPrice' :    key_code = '738'
    elif key_name == 'sZizenCyouseiC' :    key_code = '739'
    elif key_name == 'sZizenCyouseiCYoku' :    key_code = '740'
    elif key_name == 'sZougen' :    key_code = '741'
    elif key_name == 'sZyouhouCode' :    key_code = '742'
    elif key_name == 'sZyouhouSource' :    key_code = '743'
    elif key_name == 'sZyoutoekiKazeiC' :    key_code = '744'
    elif key_name == 'sZyouzyouHaisiDay' :    key_code = '745'
    elif key_name == 'sZyouzyouHakkouKabusu' :    key_code = '746'
    elif key_name == 'sZyouzyouKubun' :    key_code = '747'
    elif key_name == 'sZyouzyouNyusatuC' :    key_code = '748'
    elif key_name == 'sZyouzyouOutouDay' :    key_code = '749'
    elif key_name == 'sZyouzyouSizyou' :    key_code = '750'
    elif key_name == 'tDHP:T' :    key_code = '751'
    elif key_name == 'tDLP:T' :    key_code = '752'
    elif key_name == 'tDOP:T' :    key_code = '753'
    elif key_name == 'tDPP:T' :    key_code = '754'
    elif key_name == 'xDCFS' :    key_code = '755'
    elif key_name == 'xDVES' :    key_code = '756'
    elif key_name == 'xLISS' :    key_code = '757'
    else:
        key_code = 'nocode'
        print('項目名が有りません ', key_name)
        
    return key_code



# 返値： 項目名
# 引数1： 項目番号
def func_code2name( key_code ):
    if key_code == '1' :    key_name = 'CLMAuthCheckSecondPassword'
    elif key_code == '2' :    key_name = 'CLMAuthLoginAck'
    elif key_code == '3' :    key_name = 'CLMAuthLoginRequest'
    elif key_code == '4' :    key_name = 'CLMAuthLogoutAck'
    elif key_code == '5' :    key_name = 'CLMAuthLogoutRequest'
    elif key_code == '6' :    key_name = 'CLMAuthSessionInfo'
    elif key_code == '7' :    key_name = 'CLMAuthStkLoginRequest'
    elif key_code == '8' :    key_name = 'CLMDaiyouKakeme'
    elif key_code == '9' :    key_name = 'CLMDateZyouhou'
    elif key_code == '10' :    key_name = 'CLMEventDownload'
    elif key_code == '11' :    key_name = 'CLMEventDownloadComplete'
    elif key_code == '12' :    key_name = 'CLMGenbutuKabuList'
    elif key_code == '13' :    key_name = 'CLMHosyoukinMst'
    elif key_code == '14' :    key_name = 'CLMIssueMstKabu'
    elif key_code == '15' :    key_name = 'CLMIssueMstOp'
    elif key_code == '16' :    key_name = 'CLMIssueMstSak'
    elif key_code == '17' :    key_name = 'CLMIssueSizyouKiseiHasei'
    elif key_code == '18' :    key_name = 'CLMIssueSizyouKiseiKabu'
    elif key_code == '19' :    key_name = 'CLMIssueSizyouMstKabu'
    elif key_code == '20' :    key_name = 'CLMKabuCancelOrder'
    elif key_code == '21' :    key_name = 'CLMKabuCorrectOrder'
    elif key_code == '22' :    key_name = 'CLMKabuNewOrder'
    elif key_code == '23' :    key_name = 'CLMKabuOrderUpdateEvent'
    elif key_code == '24' :    key_name = 'CLMKeepAliveRequest'
    elif key_code == '25' :    key_name = 'CLMMfdsEventData'
    elif key_code == '26' :    key_name = 'CLMMfdsGetEventData'
    elif key_code == '27' :    key_name = 'CLMMfdsGetEventNoLast'
    elif key_code == '28' :    key_name = 'CLMMfdsGetMasterData'
    elif key_code == '29' :    key_name = 'CLMMsgTable'
    elif key_code == '30' :    key_name = 'CLMOrderErrReason'
    elif key_code == '31' :    key_name = 'CLMOrderList'
    elif key_code == '32' :    key_name = 'CLMOrderListDetail'
    elif key_code == '33' :    key_name = 'CLMShinyouTategyokuList'
    elif key_code == '34' :    key_name = 'CLMSystemStatus'
    elif key_code == '35' :    key_name = 'CLMUnyouStatus'
    elif key_code == '36' :    key_name = 'CLMUnyouStatusHasei'
    elif key_code == '37' :    key_name = 'CLMUnyouStatusKabu'
    elif key_code == '38' :    key_name = 'CLMYobine'
    elif key_code == '39' :    key_name = 'CLMZanKaiGenbutuKaitukeSyousai'
    elif key_code == '40' :    key_name = 'CLMZanKaiKanougaku'
    elif key_code == '41' :    key_name = 'CLMZanKaiKanougakuSuii'
    elif key_code == '42' :    key_name = 'CLMZanKaiSinyouSinkidateSyousai'
    elif key_code == '43' :    key_name = 'CLMZanKaiSummary'
    elif key_code == '44' :    key_name = 'CLMZanRealHosyoukinRitu'
    elif key_code == '45' :    key_name = 'CLMZanShinkiKanoIjiritu'
    elif key_code == '46' :    key_name = 'CLMZanUriKanousuu'
    elif key_code == '47' :    key_name = 'aCLMKabuHensaiData'
    elif key_code == '48' :    key_name = 'aCLMMfdsMarketPrice'
    elif key_code == '49' :    key_name = 'aGenbutuKabuList'
    elif key_code == '50' :    key_name = 'aHikazeiKouzaList'
    elif key_code == '51' :    key_name = 'aHosyoukinSeikyuZyoukyouList'
    elif key_code == '52' :    key_name = 'aKanougakuSuiiList'
    elif key_code == '53' :    key_name = 'aKessaiOrderTategyokuList'
    elif key_code == '54' :    key_name = 'aOisyouHasseiZyoukyouList'
    elif key_code == '55' :    key_name = 'aOrderList'
    elif key_code == '56' :    key_name = 'aShinyouTategyokuList'
    elif key_code == '57' :    key_name = 'aYakuzyouSikkouList'
    elif key_code == '58' :    key_name = 'pAAV'
    elif key_code == '59' :    key_name = 'pABV'
    elif key_code == '60' :    key_name = 'pAV'
    elif key_code == '61' :    key_name = 'pBICD'
    elif key_code == '62' :    key_name = 'pBV'
    elif key_code == '63' :    key_name = 'pDHF'
    elif key_code == '64' :    key_name = 'pDHP'
    elif key_code == '65' :    key_name = 'pDJ'
    elif key_code == '66' :    key_name = 'pDLF'
    elif key_code == '67' :    key_name = 'pDLP'
    elif key_code == '68' :    key_name = 'pDOP'
    elif key_code == '69' :    key_name = 'pDPG'
    elif key_code == '70' :    key_name = 'pDPP'
    elif key_code == '71' :    key_name = 'pDV'
    elif key_code == '72' :    key_name = 'pDYRP'
    elif key_code == '73' :    key_name = 'pDYWP'
    elif key_code == '74' :    key_name = 'pGAP1'
    elif key_code == '75' :    key_name = 'pGAP10'
    elif key_code == '76' :    key_name = 'pGAP2'
    elif key_code == '77' :    key_name = 'pGAP3'
    elif key_code == '78' :    key_name = 'pGAP4'
    elif key_code == '79' :    key_name = 'pGAP5'
    elif key_code == '80' :    key_name = 'pGAP6'
    elif key_code == '81' :    key_name = 'pGAP7'
    elif key_code == '82' :    key_name = 'pGAP8'
    elif key_code == '83' :    key_name = 'pGAP9'
    elif key_code == '84' :    key_name = 'pGAV1'
    elif key_code == '85' :    key_name = 'pGAV10'
    elif key_code == '86' :    key_name = 'pGAV2'
    elif key_code == '87' :    key_name = 'pGAV3'
    elif key_code == '88' :    key_name = 'pGAV4'
    elif key_code == '89' :    key_name = 'pGAV5'
    elif key_code == '90' :    key_name = 'pGAV6'
    elif key_code == '91' :    key_name = 'pGAV7'
    elif key_code == '92' :    key_name = 'pGAV8'
    elif key_code == '93' :    key_name = 'pGAV9'
    elif key_code == '94' :    key_name = 'pGBP1'
    elif key_code == '95' :    key_name = 'pGBP10'
    elif key_code == '96' :    key_name = 'pGBP2'
    elif key_code == '97' :    key_name = 'pGBP3'
    elif key_code == '98' :    key_name = 'pGBP4'
    elif key_code == '99' :    key_name = 'pGBP5'
    elif key_code == '100' :    key_name = 'pGBP6'
    elif key_code == '101' :    key_name = 'pGBP7'
    elif key_code == '102' :    key_name = 'pGBP8'
    elif key_code == '103' :    key_name = 'pGBP9'
    elif key_code == '104' :    key_name = 'pGBV1'
    elif key_code == '105' :    key_name = 'pGBV10'
    elif key_code == '106' :    key_name = 'pGBV2'
    elif key_code == '107' :    key_name = 'pGBV3'
    elif key_code == '108' :    key_name = 'pGBV4'
    elif key_code == '109' :    key_name = 'pGBV5'
    elif key_code == '110' :    key_name = 'pGBV6'
    elif key_code == '111' :    key_name = 'pGBV7'
    elif key_code == '112' :    key_name = 'pGBV8'
    elif key_code == '113' :    key_name = 'pGBV9'
    elif key_code == '114' :    key_name = 'pISCD'
    elif key_code == '115' :    key_name = 'pPRP'
    elif key_code == '116' :    key_name = 'pQAP'
    elif key_code == '117' :    key_name = 'pQAS'
    elif key_code == '118' :    key_name = 'pQBP'
    elif key_code == '119' :    key_name = 'pQBS'
    elif key_code == '120' :    key_name = 'pQOV'
    elif key_code == '121' :    key_name = 'pQUV'
    elif key_code == '122' :    key_name = 'pVWAP'
    elif key_code == '123' :    key_name = 'p_ALT'
    elif key_code == '124' :    key_name = 'p_BBKB'
    elif key_code == '125' :    key_name = 'p_CREPSR'
    elif key_code == '126' :    key_name = 'p_CREXSR'
    elif key_code == '127' :    key_name = 'p_CRPR'
    elif key_code == '128' :    key_name = 'p_CRPRKB'
    elif key_code == '129' :    key_name = 'p_CRSJ'
    elif key_code == '130' :    key_name = 'p_CRSR'
    elif key_code == '131' :    key_name = 'p_CRTKSR'
    elif key_code == '132' :    key_name = 'p_CT'
    elif key_code == '133' :    key_name = 'p_ED'
    elif key_code == '134' :    key_name = 'p_EDK'
    elif key_code == '135' :    key_name = 'p_ENO'
    elif key_code == '136' :    key_name = 'p_EPRC'
    elif key_code == '137' :    key_name = 'p_EXDT'
    elif key_code == '138' :    key_name = 'p_EXPR'
    elif key_code == '139' :    key_name = 'p_EXRC'
    elif key_code == '140' :    key_name = 'p_EXSR'
    elif key_code == '141' :    key_name = 'p_EXST'
    elif key_code == '142' :    key_name = 'p_GSCD'
    elif key_code == '143' :    key_name = 'p_IC'
    elif key_code == '144' :    key_name = 'p_IN'
    elif key_code == '145' :    key_name = 'p_KOFG'
    elif key_code == '146' :    key_name = 'p_LK'
    elif key_code == '147' :    key_name = 'p_LMIT'
    elif key_code == '148' :    key_name = 'p_MC'
    elif key_code == '149' :    key_name = 'p_NT'
    elif key_code == '150' :    key_name = 'p_ODST'
    elif key_code == '151' :    key_name = 'p_ON'
    elif key_code == '152' :    key_name = 'p_OON'
    elif key_code == '153' :    key_name = 'p_OT'
    elif key_code == '154' :    key_name = 'p_PV'
    elif key_code == '155' :    key_name = 'p_SHSB'
    elif key_code == '156' :    key_name = 'p_SS'
    elif key_code == '157' :    key_name = 'p_ST'
    elif key_code == '158' :    key_name = 'p_THKB'
    elif key_code == '159' :    key_name = 'p_TTST'
    elif key_code == '160' :    key_name = 'p_UC'
    elif key_code == '161' :    key_name = 'p_UPEXSR'
    elif key_code == '162' :    key_name = 'p_UPGKCDPR'
    elif key_code == '163' :    key_name = 'p_UPGKPR'
    elif key_code == '164' :    key_name = 'p_UPGKPRKB'
    elif key_code == '165' :    key_name = 'p_UPLMIT'
    elif key_code == '166' :    key_name = 'p_UPPR'
    elif key_code == '167' :    key_name = 'p_UPPRKB'
    elif key_code == '168' :    key_name = 'p_UPSJ'
    elif key_code == '169' :    key_name = 'p_UPSR'
    elif key_code == '170' :    key_name = 'p_US'
    elif key_code == '171' :    key_name = 'p_UU'
    elif key_code == '172' :    key_name = 'p_cmd'
    elif key_code == '173' :    key_name = 'p_err'
    elif key_code == '174' :    key_name = 'p_errno'
    elif key_code == '175' :    key_name = 'p_no'
    elif key_code == '176' :    key_name = 'p_rv_date'
    elif key_code == '177' :    key_name = 'p_sd_date'
    elif key_code == '178' :    key_name = 'sATMFlag'
    elif key_code == '179' :    key_name = 'sAzukariKin'
    elif key_code == '180' :    key_name = 'sBaDenpyouOutputUmuC'
    elif key_code == '181' :    key_name = 'sBadenpyouOutputYNC'
    elif key_code == '182' :    key_name = 'sBaiBaiDaikin'
    elif key_code == '183' :    key_name = 'sBaiBaiTesuryo'
    elif key_code == '184' :    key_name = 'sBaibaiKubun'
    elif key_code == '185' :    key_name = 'sBaibaiTani'
    elif key_code == '186' :    key_name = 'sBaibaiTaniYoku'
    elif key_code == '187' :    key_name = 'sBaibaiTeisiC'
    elif key_code == '188' :    key_name = 'sBensaiKubun'
    elif key_code == '189' :    key_name = 'sBirthDay'
    elif key_code == '190' :    key_name = 'sBondUkewatasiDay'
    elif key_code == '191' :    key_name = 'sButenCode'
    elif key_code == '192' :    key_name = 'sCLMID'
    elif key_code == '193' :    key_name = 'sChannel'
    elif key_code == '194' :    key_name = 'sCheckOnly'
    elif key_code == '195' :    key_name = 'sCondition'
    elif key_code == '196' :    key_name = 'sConditionTeiseiFlg'
    elif key_code == '197' :    key_name = 'sCreateDate'
    elif key_code == '198' :    key_name = 'sCreateTime'
    elif key_code == '199' :    key_name = 'sDaiyoHosyokinRitu'
    elif key_code == '200' :    key_name = 'sDaiyouHyoukaTanka'
    elif key_code == '201' :    key_name = 'sDaiyouHyoukagaku'
    elif key_code == '202' :    key_name = 'sDateTime'
    elif key_code == '203' :    key_name = 'sDayKey'
    elif key_code == '204' :    key_name = 'sDecimal_1'
    elif key_code == '205' :    key_name = 'sDecimal_10'
    elif key_code == '206' :    key_name = 'sDecimal_11'
    elif key_code == '207' :    key_name = 'sDecimal_12'
    elif key_code == '208' :    key_name = 'sDecimal_13'
    elif key_code == '209' :    key_name = 'sDecimal_14'
    elif key_code == '210' :    key_name = 'sDecimal_15'
    elif key_code == '211' :    key_name = 'sDecimal_16'
    elif key_code == '212' :    key_name = 'sDecimal_17'
    elif key_code == '213' :    key_name = 'sDecimal_18'
    elif key_code == '214' :    key_name = 'sDecimal_19'
    elif key_code == '215' :    key_name = 'sDecimal_2'
    elif key_code == '216' :    key_name = 'sDecimal_20'
    elif key_code == '217' :    key_name = 'sDecimal_3'
    elif key_code == '218' :    key_name = 'sDecimal_4'
    elif key_code == '219' :    key_name = 'sDecimal_5'
    elif key_code == '220' :    key_name = 'sDecimal_6'
    elif key_code == '221' :    key_name = 'sDecimal_7'
    elif key_code == '222' :    key_name = 'sDecimal_8'
    elif key_code == '223' :    key_name = 'sDecimal_9'
    elif key_code == '224' :    key_name = 'sDeleteDay'
    elif key_code == '225' :    key_name = 'sDeleteFlag'
    elif key_code == '226' :    key_name = 'sDeleteTime'
    elif key_code == '227' :    key_name = 'sEigyouDay'
    elif key_code == '228' :    key_name = 'sEigyouDayC'
    elif key_code == '229' :    key_name = 'sErrReasonCode'
    elif key_code == '230' :    key_name = 'sErrReasonText'
    elif key_code == '231' :    key_name = 'sEventName'
    elif key_code == '232' :    key_name = 'sFurikaeKouzaKubun'
    elif key_code == '233' :    key_name = 'sFusokugaku'
    elif key_code == '234' :    key_name = 'sGaikokuKouzaKubun'
    elif key_code == '235' :    key_name = 'sGaisanDaikin'
    elif key_code == '236' :    key_name = 'sGenbikiKanougaku'
    elif key_code == '237' :    key_name = 'sGenbikiWatasiHosyoukin'
    elif key_code == '238' :    key_name = 'sGenbikiWatasiTateDaikin'
    elif key_code == '239' :    key_name = 'sGenbutuBaibaiDaikin'
    elif key_code == '240' :    key_name = 'sGenbutuKabuKaituke'
    elif key_code == '241' :    key_name = 'sGenbutuKaituke'
    elif key_code == '242' :    key_name = 'sGenbutuKaitukeKanougaku'
    elif key_code == '243' :    key_name = 'sGenbutuKaitukeYoku'
    elif key_code == '244' :    key_name = 'sGenbutuOrderCount'
    elif key_code == '245' :    key_name = 'sGenbutuUrituke'
    elif key_code == '246' :    key_name = 'sGenbutuUritukeYoku'
    elif key_code == '247' :    key_name = 'sGenbutuYakuzyouCount'
    elif key_code == '248' :    key_name = 'sGenbutuZyoutoekiKazeiC'
    elif key_code == '249' :    key_name = 'sGenbwGenkinHosyoukin'
    elif key_code == '250' :    key_name = 'sGengetu'
    elif key_code == '251' :    key_name = 'sGenkinHosyokinRitu'
    elif key_code == '252' :    key_name = 'sGenkinHosyoukin'
    elif key_code == '253' :    key_name = 'sGenkinHosyoukinYoryoku'
    elif key_code == '254' :    key_name = 'sGenkinShinyouKubun'
    elif key_code == '255' :    key_name = 'sGenkinSinyouKubun'
    elif key_code == '256' :    key_name = 'sGensisanCode'
    elif key_code == '257' :    key_name = 'sGensisanKubun'
    elif key_code == '258' :    key_name = 'sGyakusasiKubun'
    elif key_code == '259' :    key_name = 'sGyakusasiOrderType'
    elif key_code == '260' :    key_name = 'sGyakusasiPrice'
    elif key_code == '261' :    key_name = 'sGyakusasiPriceKubun'
    elif key_code == '262' :    key_name = 'sGyakusasiPriceTeiseiFlg'
    elif key_code == '263' :    key_name = 'sGyakusasiZyouken'
    elif key_code == '264' :    key_name = 'sGyakusasiZyoukenTeiseiFlg'
    elif key_code == '265' :    key_name = 'sGyoumuZyoutai'
    elif key_code == '266' :    key_name = 'sGyousyuCode'
    elif key_code == '267' :    key_name = 'sGyousyuName'
    elif key_code == '268' :    key_name = 'sHakkouKaisiDay'
    elif key_code == '269' :    key_name = 'sHakkouSaisyuDay'
    elif key_code == '270' :    key_name = 'sHattyuDaiyouHyoukagaku'
    elif key_code == '271' :    key_name = 'sHattyuGenkinHosyoukin'
    elif key_code == '272' :    key_name = 'sHattyuHosyoukin'
    elif key_code == '273' :    key_name = 'sHattyuTateDaikin'
    elif key_code == '274' :    key_name = 'sHattyuZyutoukin'
    elif key_code == '275' :    key_name = 'sHenkouDay'
    elif key_code == '276' :    key_name = 'sHibakariKousokukin'
    elif key_code == '277' :    key_name = 'sHibuGyakuhibuKousokukin'
    elif key_code == '278' :    key_name = 'sHibuGyakuhibuSyueki'
    elif key_code == '279' :    key_name = 'sHikazeiC'
    elif key_code == '280' :    key_name = 'sHikazeiKouzaKaituke'
    elif key_code == '281' :    key_name = 'sHikazeiKouzaKubun'
    elif key_code == '282' :    key_name = 'sHikazeiTekiyouYear'
    elif key_code == '283' :    key_name = 'sHituke'
    elif key_code == '284' :    key_name = 'sHitukeIndex'
    elif key_code == '285' :    key_name = 'sHituyouGenkinHosyoukin'
    elif key_code == '286' :    key_name = 'sHituyouHosyoukin'
    elif key_code == '287' :    key_name = 'sHogoAdukariKouzaKubun'
    elif key_code == '288' :    key_name = 'sHosyokinDaiyoKakeme'
    elif key_code == '289' :    key_name = 'sHosyouKinritu'
    elif key_code == '290' :    key_name = 'sHosyoukin'
    elif key_code == '291' :    key_name = 'sHosyoukinDaiyouKakeme'
    elif key_code == '292' :    key_name = 'sHosyoukinGenbutuKaitukeKanougaku'
    elif key_code == '293' :    key_name = 'sHosyoukinHikidasiKousokukin'
    elif key_code == '294' :    key_name = 'sHosyoukinHikidasiYoryoku'
    elif key_code == '295' :    key_name = 'sHosyoukinIziRitu'
    elif key_code == '296' :    key_name = 'sHosyoukinIzirituIziYoryoku'
    elif key_code == '297' :    key_name = 'sHosyoukinRitu'
    elif key_code == '298' :    key_name = 'sHosyoukinRituIziYoryoku'
    elif key_code == '299' :    key_name = 'sHosyoukinTyousyuRitu'
    elif key_code == '300' :    key_name = 'sHosyoukinYoryoku'
    elif key_code == '301' :    key_name = 'sHshzGenkinHosyoukin'
    elif key_code == '302' :    key_name = 'sHshzGenkinHosyoukinHasseiDay'
    elif key_code == '303' :    key_name = 'sHshzHosyoukin'
    elif key_code == '304' :    key_name = 'sHshzHosyoukinHasseiDay'
    elif key_code == '305' :    key_name = 'sHshzNyukinKigenDay'
    elif key_code == '306' :    key_name = 'sHusokukinHasseiFlg'
    elif key_code == '307' :    key_name = 'sHyoukaSonEki'
    elif key_code == '308' :    key_name = 'sHyoukaSonekiGoukeiKaidate'
    elif key_code == '309' :    key_name = 'sHyoukaSonekiGoukeiUridate'
    elif key_code == '310' :    key_name = 'sIPOKounyu'
    elif key_code == '311' :    key_name = 'sInsiderAgreement'
    elif key_code == '312' :    key_name = 'sIppanGaisanHyoukaSonekiGoukei'
    elif key_code == '313' :    key_name = 'sIppanGaisanHyoukagakuGoukei'
    elif key_code == '314' :    key_name = 'sIppanHyoukaSonekiGoukei'
    elif key_code == '315' :    key_name = 'sIppanSinyouGenbiki'
    elif key_code == '316' :    key_name = 'sIppanSinyouGenbikiYoku'
    elif key_code == '317' :    key_name = 'sIppanSinyouGenwatasi'
    elif key_code == '318' :    key_name = 'sIppanSinyouGenwatasiYoku'
    elif key_code == '319' :    key_name = 'sIppanSinyouKaiHensai'
    elif key_code == '320' :    key_name = 'sIppanSinyouKaiHensaiYoku'
    elif key_code == '321' :    key_name = 'sIppanSinyouSinkiKaitate'
    elif key_code == '322' :    key_name = 'sIppanSinyouSinkiKaitateYoku'
    elif key_code == '323' :    key_name = 'sIppanSinyouSinkiUritate'
    elif key_code == '324' :    key_name = 'sIppanSinyouSinkiUritateYoku'
    elif key_code == '325' :    key_name = 'sIppanSinyouUriHensai'
    elif key_code == '326' :    key_name = 'sIppanSinyouUriHensaiYoku'
    elif key_code == '327' :    key_name = 'sIssueBubetuC'
    elif key_code == '328' :    key_name = 'sIssueCode'
    elif key_code == '329' :    key_name = 'sIssueKisei1C'
    elif key_code == '330' :    key_name = 'sIssueKisei2C'
    elif key_code == '331' :    key_name = 'sIssueKubunC'
    elif key_code == '332' :    key_name = 'sIssueName'
    elif key_code == '333' :    key_name = 'sIssueNameEizi'
    elif key_code == '334' :    key_name = 'sIssueNameKana'
    elif key_code == '335' :    key_name = 'sIssueNameRyaku'
    elif key_code == '336' :    key_name = 'sItakuHosyoukinRitu'
    elif key_code == '337' :    key_name = 'sItakuhosyoukin'
    elif key_code == '338' :    key_name = 'sJsonOfmt'
    elif key_code == '339' :    key_name = 'sKabuKariUkewatasiDay'
    elif key_code == '340' :    key_name = 'sKabuUkewatasiDay'
    elif key_code == '341' :    key_name = 'sKaiHensai'
    elif key_code == '342' :    key_name = 'sKaiHensaiYoku'
    elif key_code == '343' :    key_name = 'sKaitate'
    elif key_code == '344' :    key_name = 'sKaitateDaikin'
    elif key_code == '345' :    key_name = 'sKaitateYoku'
    elif key_code == '346' :    key_name = 'sKarauriHugou'
    elif key_code == '347' :    key_name = 'sKarikessaiC'
    elif key_code == '348' :    key_name = 'sKawaseKouzaKubun'
    elif key_code == '349' :    key_name = 'sKenrikousiLastDay'
    elif key_code == '350' :    key_name = 'sKenriotiFlag'
    elif key_code == '351' :    key_name = 'sKenritukiSaisyuDay'
    elif key_code == '352' :    key_name = 'sKessaiGyakuhibu'
    elif key_code == '353' :    key_name = 'sKessaiKakikaeryou'
    elif key_code == '354' :    key_name = 'sKessaiKanrihi'
    elif key_code == '355' :    key_name = 'sKessaiKasikaburyou'
    elif key_code == '356' :    key_name = 'sKessaiOrderSuryo'
    elif key_code == '357' :    key_name = 'sKessaiSoneki'
    elif key_code == '358' :    key_name = 'sKessaiSonota'
    elif key_code == '359' :    key_name = 'sKessaiTateTesuryou'
    elif key_code == '360' :    key_name = 'sKessaiTatebiZyuni'
    elif key_code == '361' :    key_name = 'sKessaiTategyokuDay'
    elif key_code == '362' :    key_name = 'sKessaiTategyokuPrice'
    elif key_code == '363' :    key_name = 'sKessaiTotalSiteibi'
    elif key_code == '364' :    key_name = 'sKessaiTotalToday'
    elif key_code == '365' :    key_name = 'sKessaiWarningCode'
    elif key_code == '366' :    key_name = 'sKessaiWarningText'
    elif key_code == '367' :    key_name = 'sKessaiYakuzyouPrice'
    elif key_code == '368' :    key_name = 'sKessaiYakuzyouSuryo'
    elif key_code == '369' :    key_name = 'sKessaiZyunHibu'
    elif key_code == '370' :    key_name = 'sKessanC'
    elif key_code == '371' :    key_name = 'sKessanDay'
    elif key_code == '372' :    key_name = 'sKikoSankaC'
    elif key_code == '373' :    key_name = 'sKinri'
    elif key_code == '374' :    key_name = 'sKinsyouhouMidokuFlg'
    elif key_code == '375' :    key_name = 'sKizunPrice_1'
    elif key_code == '376' :    key_name = 'sKizunPrice_10'
    elif key_code == '377' :    key_name = 'sKizunPrice_11'
    elif key_code == '378' :    key_name = 'sKizunPrice_12'
    elif key_code == '379' :    key_name = 'sKizunPrice_13'
    elif key_code == '380' :    key_name = 'sKizunPrice_14'
    elif key_code == '381' :    key_name = 'sKizunPrice_15'
    elif key_code == '382' :    key_name = 'sKizunPrice_16'
    elif key_code == '383' :    key_name = 'sKizunPrice_17'
    elif key_code == '384' :    key_name = 'sKizunPrice_18'
    elif key_code == '385' :    key_name = 'sKizunPrice_19'
    elif key_code == '386' :    key_name = 'sKizunPrice_2'
    elif key_code == '387' :    key_name = 'sKizunPrice_20'
    elif key_code == '388' :    key_name = 'sKizunPrice_3'
    elif key_code == '389' :    key_name = 'sKizunPrice_4'
    elif key_code == '390' :    key_name = 'sKizunPrice_5'
    elif key_code == '391' :    key_name = 'sKizunPrice_6'
    elif key_code == '392' :    key_name = 'sKizunPrice_7'
    elif key_code == '393' :    key_name = 'sKizunPrice_8'
    elif key_code == '394' :    key_name = 'sKizunPrice_9'
    elif key_code == '395' :    key_name = 'sKoKey'
    elif key_code == '396' :    key_name = 'sKouboPrice'
    elif key_code == '397' :    key_name = 'sKousiPrice'
    elif key_code == '398' :    key_name = 'sLargeKaidateYoryoku'
    elif key_code == '399' :    key_name = 'sLargeUridateYoryoku'
    elif key_code == '400' :    key_name = 'sLastBaibaiDay'
    elif key_code == '401' :    key_name = 'sLastLoginDate'
    elif key_code == '402' :    key_name = 'sLoginKyokaKubun'
    elif key_code == '403' :    key_name = 'sMMFKouzaKubun'
    elif key_code == '404' :    key_name = 'sMRFKouzaKubun'
    elif key_code == '405' :    key_name = 'sMaeEigyouDay_1'
    elif key_code == '406' :    key_name = 'sMaeEigyouDay_2'
    elif key_code == '407' :    key_name = 'sMaeEigyouDay_3'
    elif key_code == '408' :    key_name = 'sMeisaiNumber'
    elif key_code == '409' :    key_name = 'sMeyasuTime'
    elif key_code == '410' :    key_name = 'sMikessaiGenkinHosyoukin'
    elif key_code == '411' :    key_name = 'sMikessaiTateDaikin'
    elif key_code == '412' :    key_name = 'sMiniKaidateYoryoku'
    elif key_code == '413' :    key_name = 'sMiniUridateYoryoku'
    elif key_code == '414' :    key_name = 'sMiukewatasiKessaiEki'
    elif key_code == '415' :    key_name = 'sMiukewatasiKessaiSon'
    elif key_code == '416' :    key_name = 'sMsgId'
    elif key_code == '417' :    key_name = 'sMukigenC'
    elif key_code == '418' :    key_name = 'sNearaiKubun'
    elif key_code == '419' :    key_name = 'sNehabaCheckKahiC'
    elif key_code == '420' :    key_name = 'sNehabaKigenDay'
    elif key_code == '421' :    key_name = 'sNehabaKiseiC'
    elif key_code == '422' :    key_name = 'sNehabaKiseiTi'
    elif key_code == '423' :    key_name = 'sNehabaMax'
    elif key_code == '424' :    key_name = 'sNehabaMin'
    elif key_code == '425' :    key_name = 'sNehabaSansyutuSizyouC'
    elif key_code == '426' :    key_name = 'sNehabaSizyouC'
    elif key_code == '427' :    key_name = 'sNiruiKizituC'
    elif key_code == '428' :    key_name = 'sNisaGaisanHyoukaSonekiGoukei'
    elif key_code == '429' :    key_name = 'sNisaGaisanHyoukagakuGoukei'
    elif key_code == '430' :    key_name = 'sNisaKaitukeKanougaku'
    elif key_code == '431' :    key_name = 'sNissyoukinKasikabuZan'
    elif key_code == '432' :    key_name = 'sNyusatuDay'
    elif key_code == '433' :    key_name = 'sNyusatuKaizyoDay'
    elif key_code == '434' :    key_name = 'sOhzHasseiDay'
    elif key_code == '435' :    key_name = 'sOhzHosyoukinRitu'
    elif key_code == '436' :    key_name = 'sOhzHosyoukinZougen'
    elif key_code == '437' :    key_name = 'sOhzKakuteiFlg'
    elif key_code == '438' :    key_name = 'sOhzKessaisonNyukin'
    elif key_code == '439' :    key_name = 'sOhzMikaisyouKingaku'
    elif key_code == '440' :    key_name = 'sOhzMikaisyouKingakuFlg'
    elif key_code == '441' :    key_name = 'sOhzNyukin'
    elif key_code == '442' :    key_name = 'sOhzNyukinKigenDay'
    elif key_code == '443' :    key_name = 'sOhzOisyouKingaku'
    elif key_code == '444' :    key_name = 'sOhzTategyokuKessai'
    elif key_code == '445' :    key_name = 'sOhzsDaiyouHyoukagaku'
    elif key_code == '446' :    key_name = 'sOhzsGenkinHosyoukin'
    elif key_code == '447' :    key_name = 'sOhzsHyoukaSoneki'
    elif key_code == '448' :    key_name = 'sOhzsItakuHosyoukinRitu'
    elif key_code == '449' :    key_name = 'sOhzsKeisanDay'
    elif key_code == '450' :    key_name = 'sOhzsMiukeKessaiEki'
    elif key_code == '451' :    key_name = 'sOhzsMiukeKessaiSon'
    elif key_code == '452' :    key_name = 'sOhzsSasiireHosyoukin'
    elif key_code == '453' :    key_name = 'sOhzsSyokeihi'
    elif key_code == '454' :    key_name = 'sOhzsTatekabuDaikin'
    elif key_code == '455' :    key_name = 'sOhzsUkeireHosyoukin'
    elif key_code == '456' :    key_name = 'sOisyouHasseiFlg'
    elif key_code == '457' :    key_name = 'sOisyouHituyouHosyoukin'
    elif key_code == '458' :    key_name = 'sOisyouKakuteiFlg'
    elif key_code == '459' :    key_name = 'sOisyouYoryoku'
    elif key_code == '460' :    key_name = 'sOogutiKabusu'
    elif key_code == '461' :    key_name = 'sOogutiKingaku'
    elif key_code == '462' :    key_name = 'sOpBaibaiDaikin'
    elif key_code == '463' :    key_name = 'sOpKaidateYoryokyu'
    elif key_code == '464' :    key_name = 'sOpOrderCount'
    elif key_code == '465' :    key_name = 'sOpYakuzyouCount'
    elif key_code == '466' :    key_name = 'sOrderAcceptTime'
    elif key_code == '467' :    key_name = 'sOrderBaibaiKubun'
    elif key_code == '468' :    key_name = 'sOrderBensaiKubun'
    elif key_code == '469' :    key_name = 'sOrderCondition'
    elif key_code == '470' :    key_name = 'sOrderCorrectCancelKahiFlg'
    elif key_code == '471' :    key_name = 'sOrderCurrentSuryou'
    elif key_code == '472' :    key_name = 'sOrderDate'
    elif key_code == '473' :    key_name = 'sOrderExpireDay'
    elif key_code == '474' :    key_name = 'sOrderExpireDayTeiseiFlg'
    elif key_code == '475' :    key_name = 'sOrderGaisanHyoukaSoneki'
    elif key_code == '476' :    key_name = 'sOrderGaisanHyoukaSonekiRitu'
    elif key_code == '477' :    key_name = 'sOrderGenbikiGenwatasiKabusu'
    elif key_code == '478' :    key_name = 'sOrderGyakuhibu'
    elif key_code == '479' :    key_name = 'sOrderGyakusasiKubun'
    elif key_code == '480' :    key_name = 'sOrderGyakusasiOrderType'
    elif key_code == '481' :    key_name = 'sOrderGyakusasiPrice'
    elif key_code == '482' :    key_name = 'sOrderGyakusasiZyouken'
    elif key_code == '483' :    key_name = 'sOrderHensaiKanouSuryou'
    elif key_code == '484' :    key_name = 'sOrderHyoukaTanka'
    elif key_code == '485' :    key_name = 'sOrderIssueCode'
    elif key_code == '486' :    key_name = 'sOrderKakikaeryou'
    elif key_code == '487' :    key_name = 'sOrderKanrihi'
    elif key_code == '488' :    key_name = 'sOrderKasikaburyou'
    elif key_code == '489' :    key_name = 'sOrderKurikosiOrderFlg'
    elif key_code == '490' :    key_name = 'sOrderNumber'
    elif key_code == '491' :    key_name = 'sOrderOrderDateTime'
    elif key_code == '492' :    key_name = 'sOrderOrderExpireDay'
    elif key_code == '493' :    key_name = 'sOrderOrderNumber'
    elif key_code == '494' :    key_name = 'sOrderOrderPrice'
    elif key_code == '495' :    key_name = 'sOrderOrderPriceKubun'
    elif key_code == '496' :    key_name = 'sOrderOrderSuryou'
    elif key_code == '497' :    key_name = 'sOrderPrice'
    elif key_code == '498' :    key_name = 'sOrderPriceKubun'
    elif key_code == '499' :    key_name = 'sOrderPriceTeiseiFlg'
    elif key_code == '500' :    key_name = 'sOrderSikkouDay'
    elif key_code == '501' :    key_name = 'sOrderSizyouC'
    elif key_code == '502' :    key_name = 'sOrderSonota'
    elif key_code == '503' :    key_name = 'sOrderStatus'
    elif key_code == '504' :    key_name = 'sOrderStatusCode'
    elif key_code == '505' :    key_name = 'sOrderSuryou'
    elif key_code == '506' :    key_name = 'sOrderSuryouTeiseiFlg'
    elif key_code == '507' :    key_name = 'sOrderSyouhizei'
    elif key_code == '508' :    key_name = 'sOrderSyoukaiStatus'
    elif key_code == '509' :    key_name = 'sOrderTateTesuryou'
    elif key_code == '510' :    key_name = 'sOrderTatebiType'
    elif key_code == '511' :    key_name = 'sOrderTategyokuDay'
    elif key_code == '512' :    key_name = 'sOrderTategyokuKizituDay'
    elif key_code == '513' :    key_name = 'sOrderTategyokuNumber'
    elif key_code == '514' :    key_name = 'sOrderTategyokuSuryou'
    elif key_code == '515' :    key_name = 'sOrderTategyokuTanka'
    elif key_code == '516' :    key_name = 'sOrderTesuryou'
    elif key_code == '517' :    key_name = 'sOrderTriggerType'
    elif key_code == '518' :    key_name = 'sOrderType'
    elif key_code == '519' :    key_name = 'sOrderUkewatasiKingaku'
    elif key_code == '520' :    key_name = 'sOrderUtidekiKbn'
    elif key_code == '521' :    key_name = 'sOrderWarningCode'
    elif key_code == '522' :    key_name = 'sOrderWarningText'
    elif key_code == '523' :    key_name = 'sOrderYakuzyouHensaiKabusu'
    elif key_code == '524' :    key_name = 'sOrderYakuzyouPrice'
    elif key_code == '525' :    key_name = 'sOrderYakuzyouStatus'
    elif key_code == '526' :    key_name = 'sOrderYakuzyouSuryo'
    elif key_code == '527' :    key_name = 'sOrderZougen'
    elif key_code == '528' :    key_name = 'sOrderZyoutoekiKazeiC'
    elif key_code == '529' :    key_name = 'sOrderZyunHibu'
    elif key_code == '530' :    key_name = 'sOyaKey'
    elif key_code == '531' :    key_name = 'sPassword'
    elif key_code == '532' :    key_name = 'sPutCall'
    elif key_code == '533' :    key_name = 'sReason'
    elif key_code == '534' :    key_name = 'sResultCode'
    elif key_code == '535' :    key_name = 'sResultText'
    elif key_code == '536' :    key_name = 'sRuitouKaituke'
    elif key_code == '537' :    key_name = 'sSaiteiHituyouHosyoukin'
    elif key_code == '538' :    key_name = 'sSakOpSyouhin'
    elif key_code == '539' :    key_name = 'sSakiBaibaiDaikin'
    elif key_code == '540' :    key_name = 'sSakiOrderCount'
    elif key_code == '541' :    key_name = 'sSakiYakuzyouCount'
    elif key_code == '542' :    key_name = 'sSakopKouzaKubun'
    elif key_code == '543' :    key_name = 'sSasiireHosyoukin'
    elif key_code == '544' :    key_name = 'sSecondPassword'
    elif key_code == '545' :    key_name = 'sSecondPasswordOmit'
    elif key_code == '546' :    key_name = 'sSeidoSinyouGenbiki'
    elif key_code == '547' :    key_name = 'sSeidoSinyouGenbikiYoku'
    elif key_code == '548' :    key_name = 'sSeidoSinyouGenwatasi'
    elif key_code == '549' :    key_name = 'sSeidoSinyouGenwatasiYoku'
    elif key_code == '550' :    key_name = 'sSeidoSinyouKaiHensai'
    elif key_code == '551' :    key_name = 'sSeidoSinyouKaiHensaiYoku'
    elif key_code == '552' :    key_name = 'sSeidoSinyouSinkiKaitate'
    elif key_code == '553' :    key_name = 'sSeidoSinyouSinkiKaitateYoku'
    elif key_code == '554' :    key_name = 'sSeidoSinyouSinkiUritate'
    elif key_code == '555' :    key_name = 'sSeidoSinyouSinkiUritateYoku'
    elif key_code == '556' :    key_name = 'sSeidoSinyouUriHensai'
    elif key_code == '557' :    key_name = 'sSeidoSinyouUriHensaiYoku'
    elif key_code == '558' :    key_name = 'sShouhizei'
    elif key_code == '559' :    key_name = 'sSikkouDay'
    elif key_code == '560' :    key_name = 'sSinkiTesuryou'
    elif key_code == '561' :    key_name = 'sSinkiZyouzyouDay'
    elif key_code == '562' :    key_name = 'sSinyouBaibaiDaikin'
    elif key_code == '563' :    key_name = 'sSinyouC'
    elif key_code == '564' :    key_name = 'sSinyouGenbiki'
    elif key_code == '565' :    key_name = 'sSinyouKouzaKubun'
    elif key_code == '566' :    key_name = 'sSinyouOrderCount'
    elif key_code == '567' :    key_name = 'sSinyouSinkidate'
    elif key_code == '568' :    key_name = 'sSinyouSinkidateKanougaku'
    elif key_code == '569' :    key_name = 'sSinyouSyutyuKubun'
    elif key_code == '570' :    key_name = 'sSinyouSyutyuKubunYoku'
    elif key_code == '571' :    key_name = 'sSinyouTadeSyoukeihi'
    elif key_code == '572' :    key_name = 'sSinyouTateHyoukaEki'
    elif key_code == '573' :    key_name = 'sSinyouTateHyoukaSon'
    elif key_code == '574' :    key_name = 'sSinyouYakuzyouCount'
    elif key_code == '575' :    key_name = 'sSinyouZyoutoekiKazeiC'
    elif key_code == '576' :    key_name = 'sSizyouC'
    elif key_code == '577' :    key_name = 'sSizyouErrorCode'
    elif key_code == '578' :    key_name = 'sSizyoubetuBaibaiTani'
    elif key_code == '579' :    key_name = 'sSizyoubetuBaibaiTaniYoku'
    elif key_code == '580' :    key_name = 'sSogoKouzaKubun'
    elif key_code == '581' :    key_name = 'sSokuzituNyukinC'
    elif key_code == '582' :    key_name = 'sSokuzituNyukinCYoku'
    elif key_code == '583' :    key_name = 'sSokuzituNyukinKiseiDate'
    elif key_code == '584' :    key_name = 'sSonotaKousokukin'
    elif key_code == '585' :    key_name = 'sSonotaTateSyokeihi'
    elif key_code == '586' :    key_name = 'sSorC'
    elif key_code == '587' :    key_name = 'sStkSessionId'
    elif key_code == '588' :    key_name = 'sSummaryGenkabuKaituke'
    elif key_code == '589' :    key_name = 'sSummaryNisaKaitukeKanougaku'
    elif key_code == '590' :    key_name = 'sSummarySinyouSinkidate'
    elif key_code == '591' :    key_name = 'sSummaryUpdate'
    elif key_code == '592' :    key_name = 'sSyouhinType'
    elif key_code == '593' :    key_name = 'sSyoukokinFusokugaku'
    elif key_code == '594' :    key_name = 'sSystemC'
    elif key_code == '595' :    key_name = 'sSystemKouzaKubun'
    elif key_code == '596' :    key_name = 'sSystemStatus'
    elif key_code == '597' :    key_name = 'sSystemStatusKey'
    elif key_code == '598' :    key_name = 'sSyukkin'
    elif key_code == '599' :    key_name = 'sSyukkinKanougaku'
    elif key_code == '600' :    key_name = 'sSyuzituOwarine'
    elif key_code == '601' :    key_name = 'sT0HyoukaSonEki'
    elif key_code == '602' :    key_name = 'sT0ItakuHosyoukinRitu'
    elif key_code == '603' :    key_name = 'sT0OisyouHituyouHosyoukin'
    elif key_code == '604' :    key_name = 'sT0OisyouYoryoku'
    elif key_code == '605' :    key_name = 'sT0SasiireHosyoukin'
    elif key_code == '606' :    key_name = 'sT0TateKabuDaikin'
    elif key_code == '607' :    key_name = 'sT0UkeireHosyoukin'
    elif key_code == '608' :    key_name = 'sT5HyoukaSonEki'
    elif key_code == '609' :    key_name = 'sT5ItakuHosyoukinRitu'
    elif key_code == '610' :    key_name = 'sT5OisyouHituyouHosyoukin'
    elif key_code == '611' :    key_name = 'sT5OisyouYoryoku'
    elif key_code == '612' :    key_name = 'sT5SasiireHosyoukin'
    elif key_code == '613' :    key_name = 'sT5TateKabuDaikin'
    elif key_code == '614' :    key_name = 'sT5UkeireHosyoukin'
    elif key_code == '615' :    key_name = 'sTaisyouGyoumu'
    elif key_code == '616' :    key_name = 'sTaniSuryou'
    elif key_code == '617' :    key_name = 'sTargetCLMID'
    elif key_code == '618' :    key_name = 'sTargetColumn'
    elif key_code == '619' :    key_name = 'sTateKabuDaikin'
    elif key_code == '620' :    key_name = 'sTatebiType'
    elif key_code == '621' :    key_name = 'sTatebiZyuni'
    elif key_code == '622' :    key_name = 'sTategyokuDaikin'
    elif key_code == '623' :    key_name = 'sTategyokuNumber'
    elif key_code == '624' :    key_name = 'sTategyokuSuryou'
    elif key_code == '625' :    key_name = 'sTategyokuZyoutoekiKazeiC'
    elif key_code == '626' :    key_name = 'sTatekabuHyoukaSoneki'
    elif key_code == '627' :    key_name = 'sTatekabuSyoukeihi'
    elif key_code == '628' :    key_name = 'sTatekaekinHasseiFlg'
    elif key_code == '629' :    key_name = 'sTeisiKubun'
    elif key_code == '630' :    key_name = 'sTekiyouDay'
    elif key_code == '631' :    key_name = 'sText'
    elif key_code == '632' :    key_name = 'sTheDay'
    elif key_code == '633' :    key_name = 'sThzHibakariKousokukin'
    elif key_code == '634' :    key_name = 'sThzHituyouNyukingaku'
    elif key_code == '635' :    key_name = 'sThzHurikaegaku'
    elif key_code == '636' :    key_name = 'sThzKakuteiFlg'
    elif key_code == '637' :    key_name = 'sThzNyukinKigenDay'
    elif key_code == '638' :    key_name = 'sThzSeisangaku'
    elif key_code == '639' :    key_name = 'sTokuteiF'
    elif key_code == '640' :    key_name = 'sTokuteiGaisanHyoukaSonekiGoukei'
    elif key_code == '641' :    key_name = 'sTokuteiGaisanHyoukagakuGoukei'
    elif key_code == '642' :    key_name = 'sTokuteiHaitouKouzaKubun'
    elif key_code == '643' :    key_name = 'sTokuteiHyoukaSonekiGoukei'
    elif key_code == '644' :    key_name = 'sTokuteiKanriKouzaKubun'
    elif key_code == '645' :    key_name = 'sTokuteiKouzaKubunGenbutu'
    elif key_code == '646' :    key_name = 'sTokuteiKouzaKubunSinyou'
    elif key_code == '647' :    key_name = 'sTokuteiKouzaKubunTousin'
    elif key_code == '648' :    key_name = 'sTorihikiStartDay'
    elif key_code == '649' :    key_name = 'sTotalDaikin'
    elif key_code == '650' :    key_name = 'sTotalGaisanHyoukaSonekiGoukei'
    elif key_code == '651' :    key_name = 'sTotalGaisanHyoukagakuGoukei'
    elif key_code == '652' :    key_name = 'sTotalHyoukaSonekiGoukei'
    elif key_code == '653' :    key_name = 'sTousinKaituke'
    elif key_code == '654' :    key_name = 'sTousinKaitukeKanougaku'
    elif key_code == '655' :    key_name = 'sTouzituKessaiEki'
    elif key_code == '656' :    key_name = 'sTouzituKessaiSon'
    elif key_code == '657' :    key_name = 'sTouzituKessaiZenHyouka'
    elif key_code == '658' :    key_name = 'sTriggerTime'
    elif key_code == '659' :    key_name = 'sTriggerType'
    elif key_code == '660' :    key_name = 'sTyukokufKouzaKubun'
    elif key_code == '661' :    key_name = 'sUkeireHosyoukin'
    elif key_code == '662' :    key_name = 'sUkewatasiDay'
    elif key_code == '663' :    key_name = 'sUkewatasiTategyokuEki'
    elif key_code == '664' :    key_name = 'sUkewatasiTategyokuSon'
    elif key_code == '665' :    key_name = 'sUnyouCategory'
    elif key_code == '666' :    key_name = 'sUnyouStatus'
    elif key_code == '667' :    key_name = 'sUnyouUnit'
    elif key_code == '668' :    key_name = 'sUpDownFlag'
    elif key_code == '669' :    key_name = 'sUpdateDate'
    elif key_code == '670' :    key_name = 'sUpdateNumber'
    elif key_code == '671' :    key_name = 'sUpdateTime'
    elif key_code == '672' :    key_name = 'sUriHensai'
    elif key_code == '673' :    key_name = 'sUriHensaiYoku'
    elif key_code == '674' :    key_name = 'sUriOrderGaisanBokaTanka'
    elif key_code == '675' :    key_name = 'sUriOrderGaisanHyoukaSoneki'
    elif key_code == '676' :    key_name = 'sUriOrderGaisanHyoukaSonekiRitu'
    elif key_code == '677' :    key_name = 'sUriOrderGaisanHyoukagaku'
    elif key_code == '678' :    key_name = 'sUriOrderHyoukaTanka'
    elif key_code == '679' :    key_name = 'sUriOrderIssueCode'
    elif key_code == '680' :    key_name = 'sUriOrderUritukeKanouSuryou'
    elif key_code == '681' :    key_name = 'sUriOrderWarningCode'
    elif key_code == '682' :    key_name = 'sUriOrderWarningText'
    elif key_code == '683' :    key_name = 'sUriOrderZanKabuSuryou'
    elif key_code == '684' :    key_name = 'sUriOrderZyoutoekiKazeiC'
    elif key_code == '685' :    key_name = 'sUritate'
    elif key_code == '686' :    key_name = 'sUritateDaikin'
    elif key_code == '687' :    key_name = 'sUritateYoku'
    elif key_code == '688' :    key_name = 'sUrlEvent'
    elif key_code == '689' :    key_name = 'sUrlRequest'
    elif key_code == '690' :    key_name = 'sUserId'
    elif key_code == '691' :    key_name = 'sUtidekiKubun'
    elif key_code == '692' :    key_name = 'sWarningCode'
    elif key_code == '693' :    key_name = 'sWarningText'
    elif key_code == '694' :    key_name = 'sYakuzyouDate'
    elif key_code == '695' :    key_name = 'sYakuzyouPrice'
    elif key_code == '696' :    key_name = 'sYakuzyouSuryou'
    elif key_code == '697' :    key_name = 'sYakuzyouWarningCode'
    elif key_code == '698' :    key_name = 'sYakuzyouWarningText'
    elif key_code == '699' :    key_name = 'sYobineTaniNumber'
    elif key_code == '700' :    key_name = 'sYobineTaniNumberYoku'
    elif key_code == '701' :    key_name = 'sYobineTanka_1'
    elif key_code == '702' :    key_name = 'sYobineTanka_10'
    elif key_code == '703' :    key_name = 'sYobineTanka_11'
    elif key_code == '704' :    key_name = 'sYobineTanka_12'
    elif key_code == '705' :    key_name = 'sYobineTanka_13'
    elif key_code == '706' :    key_name = 'sYobineTanka_14'
    elif key_code == '707' :    key_name = 'sYobineTanka_15'
    elif key_code == '708' :    key_name = 'sYobineTanka_16'
    elif key_code == '709' :    key_name = 'sYobineTanka_17'
    elif key_code == '710' :    key_name = 'sYobineTanka_18'
    elif key_code == '711' :    key_name = 'sYobineTanka_19'
    elif key_code == '712' :    key_name = 'sYobineTanka_2'
    elif key_code == '713' :    key_name = 'sYobineTanka_20'
    elif key_code == '714' :    key_name = 'sYobineTanka_3'
    elif key_code == '715' :    key_name = 'sYobineTanka_4'
    elif key_code == '716' :    key_name = 'sYobineTanka_5'
    elif key_code == '717' :    key_name = 'sYobineTanka_6'
    elif key_code == '718' :    key_name = 'sYobineTanka_7'
    elif key_code == '719' :    key_name = 'sYobineTanka_8'
    elif key_code == '720' :    key_name = 'sYobineTanka_9'
    elif key_code == '721' :    key_name = 'sYokuEigyouDay_1'
    elif key_code == '722' :    key_name = 'sYokuEigyouDay_10'
    elif key_code == '723' :    key_name = 'sYokuEigyouDay_2'
    elif key_code == '724' :    key_name = 'sYokuEigyouDay_3'
    elif key_code == '725' :    key_name = 'sYokuEigyouDay_4'
    elif key_code == '726' :    key_name = 'sYokuEigyouDay_5'
    elif key_code == '727' :    key_name = 'sYokuEigyouDay_6'
    elif key_code == '728' :    key_name = 'sYokuEigyouDay_7'
    elif key_code == '729' :    key_name = 'sYokuEigyouDay_8'
    elif key_code == '730' :    key_name = 'sYokuEigyouDay_9'
    elif key_code == '731' :    key_name = 'sYusenSizyou'
    elif key_code == '732' :    key_name = 'sZanKabuSuryouUriKanouIppan'
    elif key_code == '733' :    key_name = 'sZanKabuSuryouUriKanouNisa'
    elif key_code == '734' :    key_name = 'sZanKabuSuryouUriKanouTokutei'
    elif key_code == '735' :    key_name = 'sZenzituHi'
    elif key_code == '736' :    key_name = 'sZenzituHiPer'
    elif key_code == '737' :    key_name = 'sZenzituOwarine'
    elif key_code == '738' :    key_name = 'sZenzituRironPrice'
    elif key_code == '739' :    key_name = 'sZizenCyouseiC'
    elif key_code == '740' :    key_name = 'sZizenCyouseiCYoku'
    elif key_code == '741' :    key_name = 'sZougen'
    elif key_code == '742' :    key_name = 'sZyouhouCode'
    elif key_code == '743' :    key_name = 'sZyouhouSource'
    elif key_code == '744' :    key_name = 'sZyoutoekiKazeiC'
    elif key_code == '745' :    key_name = 'sZyouzyouHaisiDay'
    elif key_code == '746' :    key_name = 'sZyouzyouHakkouKabusu'
    elif key_code == '747' :    key_name = 'sZyouzyouKubun'
    elif key_code == '748' :    key_name = 'sZyouzyouNyusatuC'
    elif key_code == '749' :    key_name = 'sZyouzyouOutouDay'
    elif key_code == '750' :    key_name = 'sZyouzyouSizyou'
    elif key_code == '751' :    key_name = 'tDHP:T'
    elif key_code == '752' :    key_name = 'tDLP:T'
    elif key_code == '753' :    key_name = 'tDOP:T'
    elif key_code == '754' :    key_name = 'tDPP:T'
    elif key_code == '755' :    key_name = 'xDCFS'
    elif key_code == '756' :    key_name = 'xDVES'
    elif key_code == '757' :    key_name = 'xLISS'


    else:
        key_code = 'nocode'
        print('項目番号が有りません ', key_code)
        
    return key_name



# 辞書形式 ------------------------------------------
# 返値： 項目名から項目番号を引ける辞書
# 引数1： なし
def func_make_dic_name_to_code():
    dic_name_to_code = { \
        'CLMAuthCheckSecondPassword':1, \
        'CLMAuthLoginAck':2, \
        'CLMAuthLoginRequest':3, \
        'CLMAuthLogoutAck':4, \
        'CLMAuthLogoutRequest':5, \
        'CLMAuthSessionInfo':6, \
        'CLMAuthStkLoginRequest':7, \
        'CLMDaiyouKakeme':8, \
        'CLMDateZyouhou':9, \
        'CLMEventDownload':10, \
        'CLMEventDownloadComplete':11, \
        'CLMGenbutuKabuList':12, \
        'CLMHosyoukinMst':13, \
        'CLMIssueMstKabu':14, \
        'CLMIssueMstOp':15, \
        'CLMIssueMstSak':16, \
        'CLMIssueSizyouKiseiHasei':17, \
        'CLMIssueSizyouKiseiKabu':18, \
        'CLMIssueSizyouMstKabu':19, \
        'CLMKabuCancelOrder':20, \
        'CLMKabuCorrectOrder':21, \
        'CLMKabuNewOrder':22, \
        'CLMKabuOrderUpdateEvent':23, \
        'CLMKeepAliveRequest':24, \
        'CLMMfdsEventData':25, \
        'CLMMfdsGetEventData':26, \
        'CLMMfdsGetEventNoLast':27, \
        'CLMMfdsGetMasterData':28, \
        'CLMMsgTable':29, \
        'CLMOrderErrReason':30, \
        'CLMOrderList':31, \
        'CLMOrderListDetail':32, \
        'CLMShinyouTategyokuList':33, \
        'CLMSystemStatus':34, \
        'CLMUnyouStatus':35, \
        'CLMUnyouStatusHasei':36, \
        'CLMUnyouStatusKabu':37, \
        'CLMYobine':38, \
        'CLMZanKaiGenbutuKaitukeSyousai':39, \
        'CLMZanKaiKanougaku':40, \
        'CLMZanKaiKanougakuSuii':41, \
        'CLMZanKaiSinyouSinkidateSyousai':42, \
        'CLMZanKaiSummary':43, \
        'CLMZanRealHosyoukinRitu':44, \
        'CLMZanShinkiKanoIjiritu':45, \
        'CLMZanUriKanousuu':46, \
        'aCLMKabuHensaiData':47, \
        'aCLMMfdsMarketPrice':48, \
        'aGenbutuKabuList':49, \
        'aHikazeiKouzaList':50, \
        'aHosyoukinSeikyuZyoukyouList':51, \
        'aKanougakuSuiiList':52, \
        'aKessaiOrderTategyokuList':53, \
        'aOisyouHasseiZyoukyouList':54, \
        'aOrderList':55, \
        'aShinyouTategyokuList':56, \
        'aYakuzyouSikkouList':57, \
        'pAAV':58, \
        'pABV':59, \
        'pAV':60, \
        'pBICD':61, \
        'pBV':62, \
        'pDHF':63, \
        'pDHP':64, \
        'pDJ':65, \
        'pDLF':66, \
        'pDLP':67, \
        'pDOP':68, \
        'pDPG':69, \
        'pDPP':70, \
        'pDV':71, \
        'pDYRP':72, \
        'pDYWP':73, \
        'pGAP1':74, \
        'pGAP10':75, \
        'pGAP2':76, \
        'pGAP3':77, \
        'pGAP4':78, \
        'pGAP5':79, \
        'pGAP6':80, \
        'pGAP7':81, \
        'pGAP8':82, \
        'pGAP9':83, \
        'pGAV1':84, \
        'pGAV10':85, \
        'pGAV2':86, \
        'pGAV3':87, \
        'pGAV4':88, \
        'pGAV5':89, \
        'pGAV6':90, \
        'pGAV7':91, \
        'pGAV8':92, \
        'pGAV9':93, \
        'pGBP1':94, \
        'pGBP10':95, \
        'pGBP2':96, \
        'pGBP3':97, \
        'pGBP4':98, \
        'pGBP5':99, \
        'pGBP6':100, \
        'pGBP7':101, \
        'pGBP8':102, \
        'pGBP9':103, \
        'pGBV1':104, \
        'pGBV10':105, \
        'pGBV2':106, \
        'pGBV3':107, \
        'pGBV4':108, \
        'pGBV5':109, \
        'pGBV6':110, \
        'pGBV7':111, \
        'pGBV8':112, \
        'pGBV9':113, \
        'pISCD':114, \
        'pPRP':115, \
        'pQAP':116, \
        'pQAS':117, \
        'pQBP':118, \
        'pQBS':119, \
        'pQOV':120, \
        'pQUV':121, \
        'pVWAP':122, \
        'p_ALT':123, \
        'p_BBKB':124, \
        'p_CREPSR':125, \
        'p_CREXSR':126, \
        'p_CRPR':127, \
        'p_CRPRKB':128, \
        'p_CRSJ':129, \
        'p_CRSR':130, \
        'p_CRTKSR':131, \
        'p_CT':132, \
        'p_ED':133, \
        'p_EDK':134, \
        'p_ENO':135, \
        'p_EPRC':136, \
        'p_EXDT':137, \
        'p_EXPR':138, \
        'p_EXRC':139, \
        'p_EXSR':140, \
        'p_EXST':141, \
        'p_GSCD':142, \
        'p_IC':143, \
        'p_IN':144, \
        'p_KOFG':145, \
        'p_LK':146, \
        'p_LMIT':147, \
        'p_MC':148, \
        'p_NT':149, \
        'p_ODST':150, \
        'p_ON':151, \
        'p_OON':152, \
        'p_OT':153, \
        'p_PV':154, \
        'p_SHSB':155, \
        'p_SS':156, \
        'p_ST':157, \
        'p_THKB':158, \
        'p_TTST':159, \
        'p_UC':160, \
        'p_UPEXSR':161, \
        'p_UPGKCDPR':162, \
        'p_UPGKPR':163, \
        'p_UPGKPRKB':164, \
        'p_UPLMIT':165, \
        'p_UPPR':166, \
        'p_UPPRKB':167, \
        'p_UPSJ':168, \
        'p_UPSR':169, \
        'p_US':170, \
        'p_UU':171, \
        'p_cmd':172, \
        'p_err':173, \
        'p_errno':174, \
        'p_no':175, \
        'p_rv_date':176, \
        'p_sd_date':177, \
        'sATMFlag':178, \
        'sAzukariKin':179, \
        'sBaDenpyouOutputUmuC':180, \
        'sBadenpyouOutputYNC':181, \
        'sBaiBaiDaikin':182, \
        'sBaiBaiTesuryo':183, \
        'sBaibaiKubun':184, \
        'sBaibaiTani':185, \
        'sBaibaiTaniYoku':186, \
        'sBaibaiTeisiC':187, \
        'sBensaiKubun':188, \
        'sBirthDay':189, \
        'sBondUkewatasiDay':190, \
        'sButenCode':191, \
        'sCLMID':192, \
        'sChannel':193, \
        'sCheckOnly':194, \
        'sCondition':195, \
        'sConditionTeiseiFlg':196, \
        'sCreateDate':197, \
        'sCreateTime':198, \
        'sDaiyoHosyokinRitu':199, \
        'sDaiyouHyoukaTanka':200, \
        'sDaiyouHyoukagaku':201, \
        'sDateTime':202, \
        'sDayKey':203, \
        'sDecimal_1':204, \
        'sDecimal_10':205, \
        'sDecimal_11':206, \
        'sDecimal_12':207, \
        'sDecimal_13':208, \
        'sDecimal_14':209, \
        'sDecimal_15':210, \
        'sDecimal_16':211, \
        'sDecimal_17':212, \
        'sDecimal_18':213, \
        'sDecimal_19':214, \
        'sDecimal_2':215, \
        'sDecimal_20':216, \
        'sDecimal_3':217, \
        'sDecimal_4':218, \
        'sDecimal_5':219, \
        'sDecimal_6':220, \
        'sDecimal_7':221, \
        'sDecimal_8':222, \
        'sDecimal_9':223, \
        'sDeleteDay':224, \
        'sDeleteFlag':225, \
        'sDeleteTime':226, \
        'sEigyouDay':227, \
        'sEigyouDayC':228, \
        'sErrReasonCode':229, \
        'sErrReasonText':230, \
        'sEventName':231, \
        'sFurikaeKouzaKubun':232, \
        'sFusokugaku':233, \
        'sGaikokuKouzaKubun':234, \
        'sGaisanDaikin':235, \
        'sGenbikiKanougaku':236, \
        'sGenbikiWatasiHosyoukin':237, \
        'sGenbikiWatasiTateDaikin':238, \
        'sGenbutuBaibaiDaikin':239, \
        'sGenbutuKabuKaituke':240, \
        'sGenbutuKaituke':241, \
        'sGenbutuKaitukeKanougaku':242, \
        'sGenbutuKaitukeYoku':243, \
        'sGenbutuOrderCount':244, \
        'sGenbutuUrituke':245, \
        'sGenbutuUritukeYoku':246, \
        'sGenbutuYakuzyouCount':247, \
        'sGenbutuZyoutoekiKazeiC':248, \
        'sGenbwGenkinHosyoukin':249, \
        'sGengetu':250, \
        'sGenkinHosyokinRitu':251, \
        'sGenkinHosyoukin':252, \
        'sGenkinHosyoukinYoryoku':253, \
        'sGenkinShinyouKubun':254, \
        'sGenkinSinyouKubun':255, \
        'sGensisanCode':256, \
        'sGensisanKubun':257, \
        'sGyakusasiKubun':258, \
        'sGyakusasiOrderType':259, \
        'sGyakusasiPrice':260, \
        'sGyakusasiPriceKubun':261, \
        'sGyakusasiPriceTeiseiFlg':262, \
        'sGyakusasiZyouken':263, \
        'sGyakusasiZyoukenTeiseiFlg':264, \
        'sGyoumuZyoutai':265, \
        'sGyousyuCode':266, \
        'sGyousyuName':267, \
        'sHakkouKaisiDay':268, \
        'sHakkouSaisyuDay':269, \
        'sHattyuDaiyouHyoukagaku':270, \
        'sHattyuGenkinHosyoukin':271, \
        'sHattyuHosyoukin':272, \
        'sHattyuTateDaikin':273, \
        'sHattyuZyutoukin':274, \
        'sHenkouDay':275, \
        'sHibakariKousokukin':276, \
        'sHibuGyakuhibuKousokukin':277, \
        'sHibuGyakuhibuSyueki':278, \
        'sHikazeiC':279, \
        'sHikazeiKouzaKaituke':280, \
        'sHikazeiKouzaKubun':281, \
        'sHikazeiTekiyouYear':282, \
        'sHituke':283, \
        'sHitukeIndex':284, \
        'sHituyouGenkinHosyoukin':285, \
        'sHituyouHosyoukin':286, \
        'sHogoAdukariKouzaKubun':287, \
        'sHosyokinDaiyoKakeme':288, \
        'sHosyouKinritu':289, \
        'sHosyoukin':290, \
        'sHosyoukinDaiyouKakeme':291, \
        'sHosyoukinGenbutuKaitukeKanougaku':292, \
        'sHosyoukinHikidasiKousokukin':293, \
        'sHosyoukinHikidasiYoryoku':294, \
        'sHosyoukinIziRitu':295, \
        'sHosyoukinIzirituIziYoryoku':296, \
        'sHosyoukinRitu':297, \
        'sHosyoukinRituIziYoryoku':298, \
        'sHosyoukinTyousyuRitu':299, \
        'sHosyoukinYoryoku':300, \
        'sHshzGenkinHosyoukin':301, \
        'sHshzGenkinHosyoukinHasseiDay':302, \
        'sHshzHosyoukin':303, \
        'sHshzHosyoukinHasseiDay':304, \
        'sHshzNyukinKigenDay':305, \
        'sHusokukinHasseiFlg':306, \
        'sHyoukaSonEki':307, \
        'sHyoukaSonekiGoukeiKaidate':308, \
        'sHyoukaSonekiGoukeiUridate':309, \
        'sIPOKounyu':310, \
        'sInsiderAgreement':311, \
        'sIppanGaisanHyoukaSonekiGoukei':312, \
        'sIppanGaisanHyoukagakuGoukei':313, \
        'sIppanHyoukaSonekiGoukei':314, \
        'sIppanSinyouGenbiki':315, \
        'sIppanSinyouGenbikiYoku':316, \
        'sIppanSinyouGenwatasi':317, \
        'sIppanSinyouGenwatasiYoku':318, \
        'sIppanSinyouKaiHensai':319, \
        'sIppanSinyouKaiHensaiYoku':320, \
        'sIppanSinyouSinkiKaitate':321, \
        'sIppanSinyouSinkiKaitateYoku':322, \
        'sIppanSinyouSinkiUritate':323, \
        'sIppanSinyouSinkiUritateYoku':324, \
        'sIppanSinyouUriHensai':325, \
        'sIppanSinyouUriHensaiYoku':326, \
        'sIssueBubetuC':327, \
        'sIssueCode':328, \
        'sIssueKisei1C':329, \
        'sIssueKisei2C':330, \
        'sIssueKubunC':331, \
        'sIssueName':332, \
        'sIssueNameEizi':333, \
        'sIssueNameKana':334, \
        'sIssueNameRyaku':335, \
        'sItakuHosyoukinRitu':336, \
        'sItakuhosyoukin':337, \
        'sJsonOfmt':338, \
        'sKabuKariUkewatasiDay':339, \
        'sKabuUkewatasiDay':340, \
        'sKaiHensai':341, \
        'sKaiHensaiYoku':342, \
        'sKaitate':343, \
        'sKaitateDaikin':344, \
        'sKaitateYoku':345, \
        'sKarauriHugou':346, \
        'sKarikessaiC':347, \
        'sKawaseKouzaKubun':348, \
        'sKenrikousiLastDay':349, \
        'sKenriotiFlag':350, \
        'sKenritukiSaisyuDay':351, \
        'sKessaiGyakuhibu':352, \
        'sKessaiKakikaeryou':353, \
        'sKessaiKanrihi':354, \
        'sKessaiKasikaburyou':355, \
        'sKessaiOrderSuryo':356, \
        'sKessaiSoneki':357, \
        'sKessaiSonota':358, \
        'sKessaiTateTesuryou':359, \
        'sKessaiTatebiZyuni':360, \
        'sKessaiTategyokuDay':361, \
        'sKessaiTategyokuPrice':362, \
        'sKessaiTotalSiteibi':363, \
        'sKessaiTotalToday':364, \
        'sKessaiWarningCode':365, \
        'sKessaiWarningText':366, \
        'sKessaiYakuzyouPrice':367, \
        'sKessaiYakuzyouSuryo':368, \
        'sKessaiZyunHibu':369, \
        'sKessanC':370, \
        'sKessanDay':371, \
        'sKikoSankaC':372, \
        'sKinri':373, \
        'sKinsyouhouMidokuFlg':374, \
        'sKizunPrice_1':375, \
        'sKizunPrice_10':376, \
        'sKizunPrice_11':377, \
        'sKizunPrice_12':378, \
        'sKizunPrice_13':379, \
        'sKizunPrice_14':380, \
        'sKizunPrice_15':381, \
        'sKizunPrice_16':382, \
        'sKizunPrice_17':383, \
        'sKizunPrice_18':384, \
        'sKizunPrice_19':385, \
        'sKizunPrice_2':386, \
        'sKizunPrice_20':387, \
        'sKizunPrice_3':388, \
        'sKizunPrice_4':389, \
        'sKizunPrice_5':390, \
        'sKizunPrice_6':391, \
        'sKizunPrice_7':392, \
        'sKizunPrice_8':393, \
        'sKizunPrice_9':394, \
        'sKoKey':395, \
        'sKouboPrice':396, \
        'sKousiPrice':397, \
        'sLargeKaidateYoryoku':398, \
        'sLargeUridateYoryoku':399, \
        'sLastBaibaiDay':400, \
        'sLastLoginDate':401, \
        'sLoginKyokaKubun':402, \
        'sMMFKouzaKubun':403, \
        'sMRFKouzaKubun':404, \
        'sMaeEigyouDay_1':405, \
        'sMaeEigyouDay_2':406, \
        'sMaeEigyouDay_3':407, \
        'sMeisaiNumber':408, \
        'sMeyasuTime':409, \
        'sMikessaiGenkinHosyoukin':410, \
        'sMikessaiTateDaikin':411, \
        'sMiniKaidateYoryoku':412, \
        'sMiniUridateYoryoku':413, \
        'sMiukewatasiKessaiEki':414, \
        'sMiukewatasiKessaiSon':415, \
        'sMsgId':416, \
        'sMukigenC':417, \
        'sNearaiKubun':418, \
        'sNehabaCheckKahiC':419, \
        'sNehabaKigenDay':420, \
        'sNehabaKiseiC':421, \
        'sNehabaKiseiTi':422, \
        'sNehabaMax':423, \
        'sNehabaMin':424, \
        'sNehabaSansyutuSizyouC':425, \
        'sNehabaSizyouC':426, \
        'sNiruiKizituC':427, \
        'sNisaGaisanHyoukaSonekiGoukei':428, \
        'sNisaGaisanHyoukagakuGoukei':429, \
        'sNisaKaitukeKanougaku':430, \
        'sNissyoukinKasikabuZan':431, \
        'sNyusatuDay':432, \
        'sNyusatuKaizyoDay':433, \
        'sOhzHasseiDay':434, \
        'sOhzHosyoukinRitu':435, \
        'sOhzHosyoukinZougen':436, \
        'sOhzKakuteiFlg':437, \
        'sOhzKessaisonNyukin':438, \
        'sOhzMikaisyouKingaku':439, \
        'sOhzMikaisyouKingakuFlg':440, \
        'sOhzNyukin':441, \
        'sOhzNyukinKigenDay':442, \
        'sOhzOisyouKingaku':443, \
        'sOhzTategyokuKessai':444, \
        'sOhzsDaiyouHyoukagaku':445, \
        'sOhzsGenkinHosyoukin':446, \
        'sOhzsHyoukaSoneki':447, \
        'sOhzsItakuHosyoukinRitu':448, \
        'sOhzsKeisanDay':449, \
        'sOhzsMiukeKessaiEki':450, \
        'sOhzsMiukeKessaiSon':451, \
        'sOhzsSasiireHosyoukin':452, \
        'sOhzsSyokeihi':453, \
        'sOhzsTatekabuDaikin':454, \
        'sOhzsUkeireHosyoukin':455, \
        'sOisyouHasseiFlg':456, \
        'sOisyouHituyouHosyoukin':457, \
        'sOisyouKakuteiFlg':458, \
        'sOisyouYoryoku':459, \
        'sOogutiKabusu':460, \
        'sOogutiKingaku':461, \
        'sOpBaibaiDaikin':462, \
        'sOpKaidateYoryokyu':463, \
        'sOpOrderCount':464, \
        'sOpYakuzyouCount':465, \
        'sOrderAcceptTime':466, \
        'sOrderBaibaiKubun':467, \
        'sOrderBensaiKubun':468, \
        'sOrderCondition':469, \
        'sOrderCorrectCancelKahiFlg':470, \
        'sOrderCurrentSuryou':471, \
        'sOrderDate':472, \
        'sOrderExpireDay':473, \
        'sOrderExpireDayTeiseiFlg':474, \
        'sOrderGaisanHyoukaSoneki':475, \
        'sOrderGaisanHyoukaSonekiRitu':476, \
        'sOrderGenbikiGenwatasiKabusu':477, \
        'sOrderGyakuhibu':478, \
        'sOrderGyakusasiKubun':479, \
        'sOrderGyakusasiOrderType':480, \
        'sOrderGyakusasiPrice':481, \
        'sOrderGyakusasiZyouken':482, \
        'sOrderHensaiKanouSuryou':483, \
        'sOrderHyoukaTanka':484, \
        'sOrderIssueCode':485, \
        'sOrderKakikaeryou':486, \
        'sOrderKanrihi':487, \
        'sOrderKasikaburyou':488, \
        'sOrderKurikosiOrderFlg':489, \
        'sOrderNumber':490, \
        'sOrderOrderDateTime':491, \
        'sOrderOrderExpireDay':492, \
        'sOrderOrderNumber':493, \
        'sOrderOrderPrice':494, \
        'sOrderOrderPriceKubun':495, \
        'sOrderOrderSuryou':496, \
        'sOrderPrice':497, \
        'sOrderPriceKubun':498, \
        'sOrderPriceTeiseiFlg':499, \
        'sOrderSikkouDay':500, \
        'sOrderSizyouC':501, \
        'sOrderSonota':502, \
        'sOrderStatus':503, \
        'sOrderStatusCode':504, \
        'sOrderSuryou':505, \
        'sOrderSuryouTeiseiFlg':506, \
        'sOrderSyouhizei':507, \
        'sOrderSyoukaiStatus':508, \
        'sOrderTateTesuryou':509, \
        'sOrderTatebiType':510, \
        'sOrderTategyokuDay':511, \
        'sOrderTategyokuKizituDay':512, \
        'sOrderTategyokuNumber':513, \
        'sOrderTategyokuSuryou':514, \
        'sOrderTategyokuTanka':515, \
        'sOrderTesuryou':516, \
        'sOrderTriggerType':517, \
        'sOrderType':518, \
        'sOrderUkewatasiKingaku':519, \
        'sOrderUtidekiKbn':520, \
        'sOrderWarningCode':521, \
        'sOrderWarningText':522, \
        'sOrderYakuzyouHensaiKabusu':523, \
        'sOrderYakuzyouPrice':524, \
        'sOrderYakuzyouStatus':525, \
        'sOrderYakuzyouSuryo':526, \
        'sOrderZougen':527, \
        'sOrderZyoutoekiKazeiC':528, \
        'sOrderZyunHibu':529, \
        'sOyaKey':530, \
        'sPassword':531, \
        'sPutCall':532, \
        'sReason':533, \
        'sResultCode':534, \
        'sResultText':535, \
        'sRuitouKaituke':536, \
        'sSaiteiHituyouHosyoukin':537, \
        'sSakOpSyouhin':538, \
        'sSakiBaibaiDaikin':539, \
        'sSakiOrderCount':540, \
        'sSakiYakuzyouCount':541, \
        'sSakopKouzaKubun':542, \
        'sSasiireHosyoukin':543, \
        'sSecondPassword':544, \
        'sSecondPasswordOmit':545, \
        'sSeidoSinyouGenbiki':546, \
        'sSeidoSinyouGenbikiYoku':547, \
        'sSeidoSinyouGenwatasi':548, \
        'sSeidoSinyouGenwatasiYoku':549, \
        'sSeidoSinyouKaiHensai':550, \
        'sSeidoSinyouKaiHensaiYoku':551, \
        'sSeidoSinyouSinkiKaitate':552, \
        'sSeidoSinyouSinkiKaitateYoku':553, \
        'sSeidoSinyouSinkiUritate':554, \
        'sSeidoSinyouSinkiUritateYoku':555, \
        'sSeidoSinyouUriHensai':556, \
        'sSeidoSinyouUriHensaiYoku':557, \
        'sShouhizei':558, \
        'sSikkouDay':559, \
        'sSinkiTesuryou':560, \
        'sSinkiZyouzyouDay':561, \
        'sSinyouBaibaiDaikin':562, \
        'sSinyouC':563, \
        'sSinyouGenbiki':564, \
        'sSinyouKouzaKubun':565, \
        'sSinyouOrderCount':566, \
        'sSinyouSinkidate':567, \
        'sSinyouSinkidateKanougaku':568, \
        'sSinyouSyutyuKubun':569, \
        'sSinyouSyutyuKubunYoku':570, \
        'sSinyouTadeSyoukeihi':571, \
        'sSinyouTateHyoukaEki':572, \
        'sSinyouTateHyoukaSon':573, \
        'sSinyouYakuzyouCount':574, \
        'sSinyouZyoutoekiKazeiC':575, \
        'sSizyouC':576, \
        'sSizyouErrorCode':577, \
        'sSizyoubetuBaibaiTani':578, \
        'sSizyoubetuBaibaiTaniYoku':579, \
        'sSogoKouzaKubun':580, \
        'sSokuzituNyukinC':581, \
        'sSokuzituNyukinCYoku':582, \
        'sSokuzituNyukinKiseiDate':583, \
        'sSonotaKousokukin':584, \
        'sSonotaTateSyokeihi':585, \
        'sSorC':586, \
        'sStkSessionId':587, \
        'sSummaryGenkabuKaituke':588, \
        'sSummaryNisaKaitukeKanougaku':589, \
        'sSummarySinyouSinkidate':590, \
        'sSummaryUpdate':591, \
        'sSyouhinType':592, \
        'sSyoukokinFusokugaku':593, \
        'sSystemC':594, \
        'sSystemKouzaKubun':595, \
        'sSystemStatus':596, \
        'sSystemStatusKey':597, \
        'sSyukkin':598, \
        'sSyukkinKanougaku':599, \
        'sSyuzituOwarine':600, \
        'sT0HyoukaSonEki':601, \
        'sT0ItakuHosyoukinRitu':602, \
        'sT0OisyouHituyouHosyoukin':603, \
        'sT0OisyouYoryoku':604, \
        'sT0SasiireHosyoukin':605, \
        'sT0TateKabuDaikin':606, \
        'sT0UkeireHosyoukin':607, \
        'sT5HyoukaSonEki':608, \
        'sT5ItakuHosyoukinRitu':609, \
        'sT5OisyouHituyouHosyoukin':610, \
        'sT5OisyouYoryoku':611, \
        'sT5SasiireHosyoukin':612, \
        'sT5TateKabuDaikin':613, \
        'sT5UkeireHosyoukin':614, \
        'sTaisyouGyoumu':615, \
        'sTaniSuryou':616, \
        'sTargetCLMID':617, \
        'sTargetColumn':618, \
        'sTateKabuDaikin':619, \
        'sTatebiType':620, \
        'sTatebiZyuni':621, \
        'sTategyokuDaikin':622, \
        'sTategyokuNumber':623, \
        'sTategyokuSuryou':624, \
        'sTategyokuZyoutoekiKazeiC':625, \
        'sTatekabuHyoukaSoneki':626, \
        'sTatekabuSyoukeihi':627, \
        'sTatekaekinHasseiFlg':628, \
        'sTeisiKubun':629, \
        'sTekiyouDay':630, \
        'sText':631, \
        'sTheDay':632, \
        'sThzHibakariKousokukin':633, \
        'sThzHituyouNyukingaku':634, \
        'sThzHurikaegaku':635, \
        'sThzKakuteiFlg':636, \
        'sThzNyukinKigenDay':637, \
        'sThzSeisangaku':638, \
        'sTokuteiF':639, \
        'sTokuteiGaisanHyoukaSonekiGoukei':640, \
        'sTokuteiGaisanHyoukagakuGoukei':641, \
        'sTokuteiHaitouKouzaKubun':642, \
        'sTokuteiHyoukaSonekiGoukei':643, \
        'sTokuteiKanriKouzaKubun':644, \
        'sTokuteiKouzaKubunGenbutu':645, \
        'sTokuteiKouzaKubunSinyou':646, \
        'sTokuteiKouzaKubunTousin':647, \
        'sTorihikiStartDay':648, \
        'sTotalDaikin':649, \
        'sTotalGaisanHyoukaSonekiGoukei':650, \
        'sTotalGaisanHyoukagakuGoukei':651, \
        'sTotalHyoukaSonekiGoukei':652, \
        'sTousinKaituke':653, \
        'sTousinKaitukeKanougaku':654, \
        'sTouzituKessaiEki':655, \
        'sTouzituKessaiSon':656, \
        'sTouzituKessaiZenHyouka':657, \
        'sTriggerTime':658, \
        'sTriggerType':659, \
        'sTyukokufKouzaKubun':660, \
        'sUkeireHosyoukin':661, \
        'sUkewatasiDay':662, \
        'sUkewatasiTategyokuEki':663, \
        'sUkewatasiTategyokuSon':664, \
        'sUnyouCategory':665, \
        'sUnyouStatus':666, \
        'sUnyouUnit':667, \
        'sUpDownFlag':668, \
        'sUpdateDate':669, \
        'sUpdateNumber':670, \
        'sUpdateTime':671, \
        'sUriHensai':672, \
        'sUriHensaiYoku':673, \
        'sUriOrderGaisanBokaTanka':674, \
        'sUriOrderGaisanHyoukaSoneki':675, \
        'sUriOrderGaisanHyoukaSonekiRitu':676, \
        'sUriOrderGaisanHyoukagaku':677, \
        'sUriOrderHyoukaTanka':678, \
        'sUriOrderIssueCode':679, \
        'sUriOrderUritukeKanouSuryou':680, \
        'sUriOrderWarningCode':681, \
        'sUriOrderWarningText':682, \
        'sUriOrderZanKabuSuryou':683, \
        'sUriOrderZyoutoekiKazeiC':684, \
        'sUritate':685, \
        'sUritateDaikin':686, \
        'sUritateYoku':687, \
        'sUrlEvent':688, \
        'sUrlRequest':689, \
        'sUserId':690, \
        'sUtidekiKubun':691, \
        'sWarningCode':692, \
        'sWarningText':693, \
        'sYakuzyouDate':694, \
        'sYakuzyouPrice':695, \
        'sYakuzyouSuryou':696, \
        'sYakuzyouWarningCode':697, \
        'sYakuzyouWarningText':698, \
        'sYobineTaniNumber':699, \
        'sYobineTaniNumberYoku':700, \
        'sYobineTanka_1':701, \
        'sYobineTanka_10':702, \
        'sYobineTanka_11':703, \
        'sYobineTanka_12':704, \
        'sYobineTanka_13':705, \
        'sYobineTanka_14':706, \
        'sYobineTanka_15':707, \
        'sYobineTanka_16':708, \
        'sYobineTanka_17':709, \
        'sYobineTanka_18':710, \
        'sYobineTanka_19':711, \
        'sYobineTanka_2':712, \
        'sYobineTanka_20':713, \
        'sYobineTanka_3':714, \
        'sYobineTanka_4':715, \
        'sYobineTanka_5':716, \
        'sYobineTanka_6':717, \
        'sYobineTanka_7':718, \
        'sYobineTanka_8':719, \
        'sYobineTanka_9':720, \
        'sYokuEigyouDay_1':721, \
        'sYokuEigyouDay_10':722, \
        'sYokuEigyouDay_2':723, \
        'sYokuEigyouDay_3':724, \
        'sYokuEigyouDay_4':725, \
        'sYokuEigyouDay_5':726, \
        'sYokuEigyouDay_6':727, \
        'sYokuEigyouDay_7':728, \
        'sYokuEigyouDay_8':729, \
        'sYokuEigyouDay_9':730, \
        'sYusenSizyou':731, \
        'sZanKabuSuryouUriKanouIppan':732, \
        'sZanKabuSuryouUriKanouNisa':733, \
        'sZanKabuSuryouUriKanouTokutei':734, \
        'sZenzituHi':735, \
        'sZenzituHiPer':736, \
        'sZenzituOwarine':737, \
        'sZenzituRironPrice':738, \
        'sZizenCyouseiC':739, \
        'sZizenCyouseiCYoku':740, \
        'sZougen':741, \
        'sZyouhouCode':742, \
        'sZyouhouSource':743, \
        'sZyoutoekiKazeiC':744, \
        'sZyouzyouHaisiDay':745, \
        'sZyouzyouHakkouKabusu':746, \
        'sZyouzyouKubun':747, \
        'sZyouzyouNyusatuC':748, \
        'sZyouzyouOutouDay':749, \
        'sZyouzyouSizyou':750, \
        'tDHP:T':751, \
        'tDLP:T':752, \
        'tDOP:T':753, \
        'tDPP:T':754, \
        'xDCFS':755, \
        'xDVES':756, \
        'xLISS':757, \
    }

    return dic_name_to_code



# 返値： 項目番号から項目名を引ける辞書
# 引数1： なし
def func_make_dic_code_to_name():
    dic_code_to_name = { \
        1:'CLMAuthCheckSecondPassword', \
        2:'CLMAuthLoginAck', \
        3:'CLMAuthLoginRequest', \
        4:'CLMAuthLogoutAck', \
        5:'CLMAuthLogoutRequest', \
        6:'CLMAuthSessionInfo', \
        7:'CLMAuthStkLoginRequest', \
        8:'CLMDaiyouKakeme', \
        9:'CLMDateZyouhou', \
        10:'CLMEventDownload', \
        11:'CLMEventDownloadComplete', \
        12:'CLMGenbutuKabuList', \
        13:'CLMHosyoukinMst', \
        14:'CLMIssueMstKabu', \
        15:'CLMIssueMstOp', \
        16:'CLMIssueMstSak', \
        17:'CLMIssueSizyouKiseiHasei', \
        18:'CLMIssueSizyouKiseiKabu', \
        19:'CLMIssueSizyouMstKabu', \
        20:'CLMKabuCancelOrder', \
        21:'CLMKabuCorrectOrder', \
        22:'CLMKabuNewOrder', \
        23:'CLMKabuOrderUpdateEvent', \
        24:'CLMKeepAliveRequest', \
        25:'CLMMfdsEventData', \
        26:'CLMMfdsGetEventData', \
        27:'CLMMfdsGetEventNoLast', \
        28:'CLMMfdsGetMasterData', \
        29:'CLMMsgTable', \
        30:'CLMOrderErrReason', \
        31:'CLMOrderList', \
        32:'CLMOrderListDetail', \
        33:'CLMShinyouTategyokuList', \
        34:'CLMSystemStatus', \
        35:'CLMUnyouStatus', \
        36:'CLMUnyouStatusHasei', \
        37:'CLMUnyouStatusKabu', \
        38:'CLMYobine', \
        39:'CLMZanKaiGenbutuKaitukeSyousai', \
        40:'CLMZanKaiKanougaku', \
        41:'CLMZanKaiKanougakuSuii', \
        42:'CLMZanKaiSinyouSinkidateSyousai', \
        43:'CLMZanKaiSummary', \
        44:'CLMZanRealHosyoukinRitu', \
        45:'CLMZanShinkiKanoIjiritu', \
        46:'CLMZanUriKanousuu', \
        47:'aCLMKabuHensaiData', \
        48:'aCLMMfdsMarketPrice', \
        49:'aGenbutuKabuList', \
        50:'aHikazeiKouzaList', \
        51:'aHosyoukinSeikyuZyoukyouList', \
        52:'aKanougakuSuiiList', \
        53:'aKessaiOrderTategyokuList', \
        54:'aOisyouHasseiZyoukyouList', \
        55:'aOrderList', \
        56:'aShinyouTategyokuList', \
        57:'aYakuzyouSikkouList', \
        58:'pAAV', \
        59:'pABV', \
        60:'pAV', \
        61:'pBICD', \
        62:'pBV', \
        63:'pDHF', \
        64:'pDHP', \
        65:'pDJ', \
        66:'pDLF', \
        67:'pDLP', \
        68:'pDOP', \
        69:'pDPG', \
        70:'pDPP', \
        71:'pDV', \
        72:'pDYRP', \
        73:'pDYWP', \
        74:'pGAP1', \
        75:'pGAP10', \
        76:'pGAP2', \
        77:'pGAP3', \
        78:'pGAP4', \
        79:'pGAP5', \
        80:'pGAP6', \
        81:'pGAP7', \
        82:'pGAP8', \
        83:'pGAP9', \
        84:'pGAV1', \
        85:'pGAV10', \
        86:'pGAV2', \
        87:'pGAV3', \
        88:'pGAV4', \
        89:'pGAV5', \
        90:'pGAV6', \
        91:'pGAV7', \
        92:'pGAV8', \
        93:'pGAV9', \
        94:'pGBP1', \
        95:'pGBP10', \
        96:'pGBP2', \
        97:'pGBP3', \
        98:'pGBP4', \
        99:'pGBP5', \
        100:'pGBP6', \
        101:'pGBP7', \
        102:'pGBP8', \
        103:'pGBP9', \
        104:'pGBV1', \
        105:'pGBV10', \
        106:'pGBV2', \
        107:'pGBV3', \
        108:'pGBV4', \
        109:'pGBV5', \
        110:'pGBV6', \
        111:'pGBV7', \
        112:'pGBV8', \
        113:'pGBV9', \
        114:'pISCD', \
        115:'pPRP', \
        116:'pQAP', \
        117:'pQAS', \
        118:'pQBP', \
        119:'pQBS', \
        120:'pQOV', \
        121:'pQUV', \
        122:'pVWAP', \
        123:'p_ALT', \
        124:'p_BBKB', \
        125:'p_CREPSR', \
        126:'p_CREXSR', \
        127:'p_CRPR', \
        128:'p_CRPRKB', \
        129:'p_CRSJ', \
        130:'p_CRSR', \
        131:'p_CRTKSR', \
        132:'p_CT', \
        133:'p_ED', \
        134:'p_EDK', \
        135:'p_ENO', \
        136:'p_EPRC', \
        137:'p_EXDT', \
        138:'p_EXPR', \
        139:'p_EXRC', \
        140:'p_EXSR', \
        141:'p_EXST', \
        142:'p_GSCD', \
        143:'p_IC', \
        144:'p_IN', \
        145:'p_KOFG', \
        146:'p_LK', \
        147:'p_LMIT', \
        148:'p_MC', \
        149:'p_NT', \
        150:'p_ODST', \
        151:'p_ON', \
        152:'p_OON', \
        153:'p_OT', \
        154:'p_PV', \
        155:'p_SHSB', \
        156:'p_SS', \
        157:'p_ST', \
        158:'p_THKB', \
        159:'p_TTST', \
        160:'p_UC', \
        161:'p_UPEXSR', \
        162:'p_UPGKCDPR', \
        163:'p_UPGKPR', \
        164:'p_UPGKPRKB', \
        165:'p_UPLMIT', \
        166:'p_UPPR', \
        167:'p_UPPRKB', \
        168:'p_UPSJ', \
        169:'p_UPSR', \
        170:'p_US', \
        171:'p_UU', \
        172:'p_cmd', \
        173:'p_err', \
        174:'p_errno', \
        175:'p_no', \
        176:'p_rv_date', \
        177:'p_sd_date', \
        178:'sATMFlag', \
        179:'sAzukariKin', \
        180:'sBaDenpyouOutputUmuC', \
        181:'sBadenpyouOutputYNC', \
        182:'sBaiBaiDaikin', \
        183:'sBaiBaiTesuryo', \
        184:'sBaibaiKubun', \
        185:'sBaibaiTani', \
        186:'sBaibaiTaniYoku', \
        187:'sBaibaiTeisiC', \
        188:'sBensaiKubun', \
        189:'sBirthDay', \
        190:'sBondUkewatasiDay', \
        191:'sButenCode', \
        192:'sCLMID', \
        193:'sChannel', \
        194:'sCheckOnly', \
        195:'sCondition', \
        196:'sConditionTeiseiFlg', \
        197:'sCreateDate', \
        198:'sCreateTime', \
        199:'sDaiyoHosyokinRitu', \
        200:'sDaiyouHyoukaTanka', \
        201:'sDaiyouHyoukagaku', \
        202:'sDateTime', \
        203:'sDayKey', \
        204:'sDecimal_1', \
        205:'sDecimal_10', \
        206:'sDecimal_11', \
        207:'sDecimal_12', \
        208:'sDecimal_13', \
        209:'sDecimal_14', \
        210:'sDecimal_15', \
        211:'sDecimal_16', \
        212:'sDecimal_17', \
        213:'sDecimal_18', \
        214:'sDecimal_19', \
        215:'sDecimal_2', \
        216:'sDecimal_20', \
        217:'sDecimal_3', \
        218:'sDecimal_4', \
        219:'sDecimal_5', \
        220:'sDecimal_6', \
        221:'sDecimal_7', \
        222:'sDecimal_8', \
        223:'sDecimal_9', \
        224:'sDeleteDay', \
        225:'sDeleteFlag', \
        226:'sDeleteTime', \
        227:'sEigyouDay', \
        228:'sEigyouDayC', \
        229:'sErrReasonCode', \
        230:'sErrReasonText', \
        231:'sEventName', \
        232:'sFurikaeKouzaKubun', \
        233:'sFusokugaku', \
        234:'sGaikokuKouzaKubun', \
        235:'sGaisanDaikin', \
        236:'sGenbikiKanougaku', \
        237:'sGenbikiWatasiHosyoukin', \
        238:'sGenbikiWatasiTateDaikin', \
        239:'sGenbutuBaibaiDaikin', \
        240:'sGenbutuKabuKaituke', \
        241:'sGenbutuKaituke', \
        242:'sGenbutuKaitukeKanougaku', \
        243:'sGenbutuKaitukeYoku', \
        244:'sGenbutuOrderCount', \
        245:'sGenbutuUrituke', \
        246:'sGenbutuUritukeYoku', \
        247:'sGenbutuYakuzyouCount', \
        248:'sGenbutuZyoutoekiKazeiC', \
        249:'sGenbwGenkinHosyoukin', \
        250:'sGengetu', \
        251:'sGenkinHosyokinRitu', \
        252:'sGenkinHosyoukin', \
        253:'sGenkinHosyoukinYoryoku', \
        254:'sGenkinShinyouKubun', \
        255:'sGenkinSinyouKubun', \
        256:'sGensisanCode', \
        257:'sGensisanKubun', \
        258:'sGyakusasiKubun', \
        259:'sGyakusasiOrderType', \
        260:'sGyakusasiPrice', \
        261:'sGyakusasiPriceKubun', \
        262:'sGyakusasiPriceTeiseiFlg', \
        263:'sGyakusasiZyouken', \
        264:'sGyakusasiZyoukenTeiseiFlg', \
        265:'sGyoumuZyoutai', \
        266:'sGyousyuCode', \
        267:'sGyousyuName', \
        268:'sHakkouKaisiDay', \
        269:'sHakkouSaisyuDay', \
        270:'sHattyuDaiyouHyoukagaku', \
        271:'sHattyuGenkinHosyoukin', \
        272:'sHattyuHosyoukin', \
        273:'sHattyuTateDaikin', \
        274:'sHattyuZyutoukin', \
        275:'sHenkouDay', \
        276:'sHibakariKousokukin', \
        277:'sHibuGyakuhibuKousokukin', \
        278:'sHibuGyakuhibuSyueki', \
        279:'sHikazeiC', \
        280:'sHikazeiKouzaKaituke', \
        281:'sHikazeiKouzaKubun', \
        282:'sHikazeiTekiyouYear', \
        283:'sHituke', \
        284:'sHitukeIndex', \
        285:'sHituyouGenkinHosyoukin', \
        286:'sHituyouHosyoukin', \
        287:'sHogoAdukariKouzaKubun', \
        288:'sHosyokinDaiyoKakeme', \
        289:'sHosyouKinritu', \
        290:'sHosyoukin', \
        291:'sHosyoukinDaiyouKakeme', \
        292:'sHosyoukinGenbutuKaitukeKanougaku', \
        293:'sHosyoukinHikidasiKousokukin', \
        294:'sHosyoukinHikidasiYoryoku', \
        295:'sHosyoukinIziRitu', \
        296:'sHosyoukinIzirituIziYoryoku', \
        297:'sHosyoukinRitu', \
        298:'sHosyoukinRituIziYoryoku', \
        299:'sHosyoukinTyousyuRitu', \
        300:'sHosyoukinYoryoku', \
        301:'sHshzGenkinHosyoukin', \
        302:'sHshzGenkinHosyoukinHasseiDay', \
        303:'sHshzHosyoukin', \
        304:'sHshzHosyoukinHasseiDay', \
        305:'sHshzNyukinKigenDay', \
        306:'sHusokukinHasseiFlg', \
        307:'sHyoukaSonEki', \
        308:'sHyoukaSonekiGoukeiKaidate', \
        309:'sHyoukaSonekiGoukeiUridate', \
        310:'sIPOKounyu', \
        311:'sInsiderAgreement', \
        312:'sIppanGaisanHyoukaSonekiGoukei', \
        313:'sIppanGaisanHyoukagakuGoukei', \
        314:'sIppanHyoukaSonekiGoukei', \
        315:'sIppanSinyouGenbiki', \
        316:'sIppanSinyouGenbikiYoku', \
        317:'sIppanSinyouGenwatasi', \
        318:'sIppanSinyouGenwatasiYoku', \
        319:'sIppanSinyouKaiHensai', \
        320:'sIppanSinyouKaiHensaiYoku', \
        321:'sIppanSinyouSinkiKaitate', \
        322:'sIppanSinyouSinkiKaitateYoku', \
        323:'sIppanSinyouSinkiUritate', \
        324:'sIppanSinyouSinkiUritateYoku', \
        325:'sIppanSinyouUriHensai', \
        326:'sIppanSinyouUriHensaiYoku', \
        327:'sIssueBubetuC', \
        328:'sIssueCode', \
        329:'sIssueKisei1C', \
        330:'sIssueKisei2C', \
        331:'sIssueKubunC', \
        332:'sIssueName', \
        333:'sIssueNameEizi', \
        334:'sIssueNameKana', \
        335:'sIssueNameRyaku', \
        336:'sItakuHosyoukinRitu', \
        337:'sItakuhosyoukin', \
        338:'sJsonOfmt', \
        339:'sKabuKariUkewatasiDay', \
        340:'sKabuUkewatasiDay', \
        341:'sKaiHensai', \
        342:'sKaiHensaiYoku', \
        343:'sKaitate', \
        344:'sKaitateDaikin', \
        345:'sKaitateYoku', \
        346:'sKarauriHugou', \
        347:'sKarikessaiC', \
        348:'sKawaseKouzaKubun', \
        349:'sKenrikousiLastDay', \
        350:'sKenriotiFlag', \
        351:'sKenritukiSaisyuDay', \
        352:'sKessaiGyakuhibu', \
        353:'sKessaiKakikaeryou', \
        354:'sKessaiKanrihi', \
        355:'sKessaiKasikaburyou', \
        356:'sKessaiOrderSuryo', \
        357:'sKessaiSoneki', \
        358:'sKessaiSonota', \
        359:'sKessaiTateTesuryou', \
        360:'sKessaiTatebiZyuni', \
        361:'sKessaiTategyokuDay', \
        362:'sKessaiTategyokuPrice', \
        363:'sKessaiTotalSiteibi', \
        364:'sKessaiTotalToday', \
        365:'sKessaiWarningCode', \
        366:'sKessaiWarningText', \
        367:'sKessaiYakuzyouPrice', \
        368:'sKessaiYakuzyouSuryo', \
        369:'sKessaiZyunHibu', \
        370:'sKessanC', \
        371:'sKessanDay', \
        372:'sKikoSankaC', \
        373:'sKinri', \
        374:'sKinsyouhouMidokuFlg', \
        375:'sKizunPrice_1', \
        376:'sKizunPrice_10', \
        377:'sKizunPrice_11', \
        378:'sKizunPrice_12', \
        379:'sKizunPrice_13', \
        380:'sKizunPrice_14', \
        381:'sKizunPrice_15', \
        382:'sKizunPrice_16', \
        383:'sKizunPrice_17', \
        384:'sKizunPrice_18', \
        385:'sKizunPrice_19', \
        386:'sKizunPrice_2', \
        387:'sKizunPrice_20', \
        388:'sKizunPrice_3', \
        389:'sKizunPrice_4', \
        390:'sKizunPrice_5', \
        391:'sKizunPrice_6', \
        392:'sKizunPrice_7', \
        393:'sKizunPrice_8', \
        394:'sKizunPrice_9', \
        395:'sKoKey', \
        396:'sKouboPrice', \
        397:'sKousiPrice', \
        398:'sLargeKaidateYoryoku', \
        399:'sLargeUridateYoryoku', \
        400:'sLastBaibaiDay', \
        401:'sLastLoginDate', \
        402:'sLoginKyokaKubun', \
        403:'sMMFKouzaKubun', \
        404:'sMRFKouzaKubun', \
        405:'sMaeEigyouDay_1', \
        406:'sMaeEigyouDay_2', \
        407:'sMaeEigyouDay_3', \
        408:'sMeisaiNumber', \
        409:'sMeyasuTime', \
        410:'sMikessaiGenkinHosyoukin', \
        411:'sMikessaiTateDaikin', \
        412:'sMiniKaidateYoryoku', \
        413:'sMiniUridateYoryoku', \
        414:'sMiukewatasiKessaiEki', \
        415:'sMiukewatasiKessaiSon', \
        416:'sMsgId', \
        417:'sMukigenC', \
        418:'sNearaiKubun', \
        419:'sNehabaCheckKahiC', \
        420:'sNehabaKigenDay', \
        421:'sNehabaKiseiC', \
        422:'sNehabaKiseiTi', \
        423:'sNehabaMax', \
        424:'sNehabaMin', \
        425:'sNehabaSansyutuSizyouC', \
        426:'sNehabaSizyouC', \
        427:'sNiruiKizituC', \
        428:'sNisaGaisanHyoukaSonekiGoukei', \
        429:'sNisaGaisanHyoukagakuGoukei', \
        430:'sNisaKaitukeKanougaku', \
        431:'sNissyoukinKasikabuZan', \
        432:'sNyusatuDay', \
        433:'sNyusatuKaizyoDay', \
        434:'sOhzHasseiDay', \
        435:'sOhzHosyoukinRitu', \
        436:'sOhzHosyoukinZougen', \
        437:'sOhzKakuteiFlg', \
        438:'sOhzKessaisonNyukin', \
        439:'sOhzMikaisyouKingaku', \
        440:'sOhzMikaisyouKingakuFlg', \
        441:'sOhzNyukin', \
        442:'sOhzNyukinKigenDay', \
        443:'sOhzOisyouKingaku', \
        444:'sOhzTategyokuKessai', \
        445:'sOhzsDaiyouHyoukagaku', \
        446:'sOhzsGenkinHosyoukin', \
        447:'sOhzsHyoukaSoneki', \
        448:'sOhzsItakuHosyoukinRitu', \
        449:'sOhzsKeisanDay', \
        450:'sOhzsMiukeKessaiEki', \
        451:'sOhzsMiukeKessaiSon', \
        452:'sOhzsSasiireHosyoukin', \
        453:'sOhzsSyokeihi', \
        454:'sOhzsTatekabuDaikin', \
        455:'sOhzsUkeireHosyoukin', \
        456:'sOisyouHasseiFlg', \
        457:'sOisyouHituyouHosyoukin', \
        458:'sOisyouKakuteiFlg', \
        459:'sOisyouYoryoku', \
        460:'sOogutiKabusu', \
        461:'sOogutiKingaku', \
        462:'sOpBaibaiDaikin', \
        463:'sOpKaidateYoryokyu', \
        464:'sOpOrderCount', \
        465:'sOpYakuzyouCount', \
        466:'sOrderAcceptTime', \
        467:'sOrderBaibaiKubun', \
        468:'sOrderBensaiKubun', \
        469:'sOrderCondition', \
        470:'sOrderCorrectCancelKahiFlg', \
        471:'sOrderCurrentSuryou', \
        472:'sOrderDate', \
        473:'sOrderExpireDay', \
        474:'sOrderExpireDayTeiseiFlg', \
        475:'sOrderGaisanHyoukaSoneki', \
        476:'sOrderGaisanHyoukaSonekiRitu', \
        477:'sOrderGenbikiGenwatasiKabusu', \
        478:'sOrderGyakuhibu', \
        479:'sOrderGyakusasiKubun', \
        480:'sOrderGyakusasiOrderType', \
        481:'sOrderGyakusasiPrice', \
        482:'sOrderGyakusasiZyouken', \
        483:'sOrderHensaiKanouSuryou', \
        484:'sOrderHyoukaTanka', \
        485:'sOrderIssueCode', \
        486:'sOrderKakikaeryou', \
        487:'sOrderKanrihi', \
        488:'sOrderKasikaburyou', \
        489:'sOrderKurikosiOrderFlg', \
        490:'sOrderNumber', \
        491:'sOrderOrderDateTime', \
        492:'sOrderOrderExpireDay', \
        493:'sOrderOrderNumber', \
        494:'sOrderOrderPrice', \
        495:'sOrderOrderPriceKubun', \
        496:'sOrderOrderSuryou', \
        497:'sOrderPrice', \
        498:'sOrderPriceKubun', \
        499:'sOrderPriceTeiseiFlg', \
        500:'sOrderSikkouDay', \
        501:'sOrderSizyouC', \
        502:'sOrderSonota', \
        503:'sOrderStatus', \
        504:'sOrderStatusCode', \
        505:'sOrderSuryou', \
        506:'sOrderSuryouTeiseiFlg', \
        507:'sOrderSyouhizei', \
        508:'sOrderSyoukaiStatus', \
        509:'sOrderTateTesuryou', \
        510:'sOrderTatebiType', \
        511:'sOrderTategyokuDay', \
        512:'sOrderTategyokuKizituDay', \
        513:'sOrderTategyokuNumber', \
        514:'sOrderTategyokuSuryou', \
        515:'sOrderTategyokuTanka', \
        516:'sOrderTesuryou', \
        517:'sOrderTriggerType', \
        518:'sOrderType', \
        519:'sOrderUkewatasiKingaku', \
        520:'sOrderUtidekiKbn', \
        521:'sOrderWarningCode', \
        522:'sOrderWarningText', \
        523:'sOrderYakuzyouHensaiKabusu', \
        524:'sOrderYakuzyouPrice', \
        525:'sOrderYakuzyouStatus', \
        526:'sOrderYakuzyouSuryo', \
        527:'sOrderZougen', \
        528:'sOrderZyoutoekiKazeiC', \
        529:'sOrderZyunHibu', \
        530:'sOyaKey', \
        531:'sPassword', \
        532:'sPutCall', \
        533:'sReason', \
        534:'sResultCode', \
        535:'sResultText', \
        536:'sRuitouKaituke', \
        537:'sSaiteiHituyouHosyoukin', \
        538:'sSakOpSyouhin', \
        539:'sSakiBaibaiDaikin', \
        540:'sSakiOrderCount', \
        541:'sSakiYakuzyouCount', \
        542:'sSakopKouzaKubun', \
        543:'sSasiireHosyoukin', \
        544:'sSecondPassword', \
        545:'sSecondPasswordOmit', \
        546:'sSeidoSinyouGenbiki', \
        547:'sSeidoSinyouGenbikiYoku', \
        548:'sSeidoSinyouGenwatasi', \
        549:'sSeidoSinyouGenwatasiYoku', \
        550:'sSeidoSinyouKaiHensai', \
        551:'sSeidoSinyouKaiHensaiYoku', \
        552:'sSeidoSinyouSinkiKaitate', \
        553:'sSeidoSinyouSinkiKaitateYoku', \
        554:'sSeidoSinyouSinkiUritate', \
        555:'sSeidoSinyouSinkiUritateYoku', \
        556:'sSeidoSinyouUriHensai', \
        557:'sSeidoSinyouUriHensaiYoku', \
        558:'sShouhizei', \
        559:'sSikkouDay', \
        560:'sSinkiTesuryou', \
        561:'sSinkiZyouzyouDay', \
        562:'sSinyouBaibaiDaikin', \
        563:'sSinyouC', \
        564:'sSinyouGenbiki', \
        565:'sSinyouKouzaKubun', \
        566:'sSinyouOrderCount', \
        567:'sSinyouSinkidate', \
        568:'sSinyouSinkidateKanougaku', \
        569:'sSinyouSyutyuKubun', \
        570:'sSinyouSyutyuKubunYoku', \
        571:'sSinyouTadeSyoukeihi', \
        572:'sSinyouTateHyoukaEki', \
        573:'sSinyouTateHyoukaSon', \
        574:'sSinyouYakuzyouCount', \
        575:'sSinyouZyoutoekiKazeiC', \
        576:'sSizyouC', \
        577:'sSizyouErrorCode', \
        578:'sSizyoubetuBaibaiTani', \
        579:'sSizyoubetuBaibaiTaniYoku', \
        580:'sSogoKouzaKubun', \
        581:'sSokuzituNyukinC', \
        582:'sSokuzituNyukinCYoku', \
        583:'sSokuzituNyukinKiseiDate', \
        584:'sSonotaKousokukin', \
        585:'sSonotaTateSyokeihi', \
        586:'sSorC', \
        587:'sStkSessionId', \
        588:'sSummaryGenkabuKaituke', \
        589:'sSummaryNisaKaitukeKanougaku', \
        590:'sSummarySinyouSinkidate', \
        591:'sSummaryUpdate', \
        592:'sSyouhinType', \
        593:'sSyoukokinFusokugaku', \
        594:'sSystemC', \
        595:'sSystemKouzaKubun', \
        596:'sSystemStatus', \
        597:'sSystemStatusKey', \
        598:'sSyukkin', \
        599:'sSyukkinKanougaku', \
        600:'sSyuzituOwarine', \
        601:'sT0HyoukaSonEki', \
        602:'sT0ItakuHosyoukinRitu', \
        603:'sT0OisyouHituyouHosyoukin', \
        604:'sT0OisyouYoryoku', \
        605:'sT0SasiireHosyoukin', \
        606:'sT0TateKabuDaikin', \
        607:'sT0UkeireHosyoukin', \
        608:'sT5HyoukaSonEki', \
        609:'sT5ItakuHosyoukinRitu', \
        610:'sT5OisyouHituyouHosyoukin', \
        611:'sT5OisyouYoryoku', \
        612:'sT5SasiireHosyoukin', \
        613:'sT5TateKabuDaikin', \
        614:'sT5UkeireHosyoukin', \
        615:'sTaisyouGyoumu', \
        616:'sTaniSuryou', \
        617:'sTargetCLMID', \
        618:'sTargetColumn', \
        619:'sTateKabuDaikin', \
        620:'sTatebiType', \
        621:'sTatebiZyuni', \
        622:'sTategyokuDaikin', \
        623:'sTategyokuNumber', \
        624:'sTategyokuSuryou', \
        625:'sTategyokuZyoutoekiKazeiC', \
        626:'sTatekabuHyoukaSoneki', \
        627:'sTatekabuSyoukeihi', \
        628:'sTatekaekinHasseiFlg', \
        629:'sTeisiKubun', \
        630:'sTekiyouDay', \
        631:'sText', \
        632:'sTheDay', \
        633:'sThzHibakariKousokukin', \
        634:'sThzHituyouNyukingaku', \
        635:'sThzHurikaegaku', \
        636:'sThzKakuteiFlg', \
        637:'sThzNyukinKigenDay', \
        638:'sThzSeisangaku', \
        639:'sTokuteiF', \
        640:'sTokuteiGaisanHyoukaSonekiGoukei', \
        641:'sTokuteiGaisanHyoukagakuGoukei', \
        642:'sTokuteiHaitouKouzaKubun', \
        643:'sTokuteiHyoukaSonekiGoukei', \
        644:'sTokuteiKanriKouzaKubun', \
        645:'sTokuteiKouzaKubunGenbutu', \
        646:'sTokuteiKouzaKubunSinyou', \
        647:'sTokuteiKouzaKubunTousin', \
        648:'sTorihikiStartDay', \
        649:'sTotalDaikin', \
        650:'sTotalGaisanHyoukaSonekiGoukei', \
        651:'sTotalGaisanHyoukagakuGoukei', \
        652:'sTotalHyoukaSonekiGoukei', \
        653:'sTousinKaituke', \
        654:'sTousinKaitukeKanougaku', \
        655:'sTouzituKessaiEki', \
        656:'sTouzituKessaiSon', \
        657:'sTouzituKessaiZenHyouka', \
        658:'sTriggerTime', \
        659:'sTriggerType', \
        660:'sTyukokufKouzaKubun', \
        661:'sUkeireHosyoukin', \
        662:'sUkewatasiDay', \
        663:'sUkewatasiTategyokuEki', \
        664:'sUkewatasiTategyokuSon', \
        665:'sUnyouCategory', \
        666:'sUnyouStatus', \
        667:'sUnyouUnit', \
        668:'sUpDownFlag', \
        669:'sUpdateDate', \
        670:'sUpdateNumber', \
        671:'sUpdateTime', \
        672:'sUriHensai', \
        673:'sUriHensaiYoku', \
        674:'sUriOrderGaisanBokaTanka', \
        675:'sUriOrderGaisanHyoukaSoneki', \
        676:'sUriOrderGaisanHyoukaSonekiRitu', \
        677:'sUriOrderGaisanHyoukagaku', \
        678:'sUriOrderHyoukaTanka', \
        679:'sUriOrderIssueCode', \
        680:'sUriOrderUritukeKanouSuryou', \
        681:'sUriOrderWarningCode', \
        682:'sUriOrderWarningText', \
        683:'sUriOrderZanKabuSuryou', \
        684:'sUriOrderZyoutoekiKazeiC', \
        685:'sUritate', \
        686:'sUritateDaikin', \
        687:'sUritateYoku', \
        688:'sUrlEvent', \
        689:'sUrlRequest', \
        690:'sUserId', \
        691:'sUtidekiKubun', \
        692:'sWarningCode', \
        693:'sWarningText', \
        694:'sYakuzyouDate', \
        695:'sYakuzyouPrice', \
        696:'sYakuzyouSuryou', \
        697:'sYakuzyouWarningCode', \
        698:'sYakuzyouWarningText', \
        699:'sYobineTaniNumber', \
        700:'sYobineTaniNumberYoku', \
        701:'sYobineTanka_1', \
        702:'sYobineTanka_10', \
        703:'sYobineTanka_11', \
        704:'sYobineTanka_12', \
        705:'sYobineTanka_13', \
        706:'sYobineTanka_14', \
        707:'sYobineTanka_15', \
        708:'sYobineTanka_16', \
        709:'sYobineTanka_17', \
        710:'sYobineTanka_18', \
        711:'sYobineTanka_19', \
        712:'sYobineTanka_2', \
        713:'sYobineTanka_20', \
        714:'sYobineTanka_3', \
        715:'sYobineTanka_4', \
        716:'sYobineTanka_5', \
        717:'sYobineTanka_6', \
        718:'sYobineTanka_7', \
        719:'sYobineTanka_8', \
        720:'sYobineTanka_9', \
        721:'sYokuEigyouDay_1', \
        722:'sYokuEigyouDay_10', \
        723:'sYokuEigyouDay_2', \
        724:'sYokuEigyouDay_3', \
        725:'sYokuEigyouDay_4', \
        726:'sYokuEigyouDay_5', \
        727:'sYokuEigyouDay_6', \
        728:'sYokuEigyouDay_7', \
        729:'sYokuEigyouDay_8', \
        730:'sYokuEigyouDay_9', \
        731:'sYusenSizyou', \
        732:'sZanKabuSuryouUriKanouIppan', \
        733:'sZanKabuSuryouUriKanouNisa', \
        734:'sZanKabuSuryouUriKanouTokutei', \
        735:'sZenzituHi', \
        736:'sZenzituHiPer', \
        737:'sZenzituOwarine', \
        738:'sZenzituRironPrice', \
        739:'sZizenCyouseiC', \
        740:'sZizenCyouseiCYoku', \
        741:'sZougen', \
        742:'sZyouhouCode', \
        743:'sZyouhouSource', \
        744:'sZyoutoekiKazeiC', \
        745:'sZyouzyouHaisiDay', \
        746:'sZyouzyouHakkouKabusu', \
        747:'sZyouzyouKubun', \
        748:'sZyouzyouNyusatuC', \
        749:'sZyouzyouOutouDay', \
        750:'sZyouzyouSizyou', \
        751:'tDHP:T', \
        752:'tDLP:T', \
        753:'tDOP:T', \
        754:'tDPP:T', \
        755:'xDCFS', \
        756:'xDVES', \
        757:'xLISS', \
    }
    return dic_code_to_name



# --- 以下テスト例 ------------------------------------------------------------- 

##dic_name_to_code = func_make_dic_name_to_code()
##str_name = 'CLMAuthLoginRequest'
##print(dic_name_to_code[str_name])
##
##
##dic_code_to_name = func_make_dic_code_to_name()
##int_code = dic_name_to_code[str_name]
##print(dic_code_to_name[int_code])
##



