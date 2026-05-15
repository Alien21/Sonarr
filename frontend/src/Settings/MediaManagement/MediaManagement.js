import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import FieldSet from 'Components/FieldSet';
import Form from 'Components/Form/Form';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import { inputTypes, kinds, sizes } from 'Helpers/Props';
import RootFolders from 'RootFolder/RootFolders';
import SettingsToolbarConnector from 'Settings/SettingsToolbarConnector';
import formatBytes from 'Utilities/Number/formatBytes';
import translate from 'Utilities/String/translate';
import Naming from './Naming/Naming';
import AddRootFolder from './RootFolder/AddRootFolder';
import styles from './MediaManagement.css';

const episodeTitleRequiredOptions = [
  {
    key: 'always',
    get value() {
      return translate('Always');
    }
  },
  {
    key: 'bulkSeasonReleases',
    get value() {
      return translate('OnlyForBulkSeasonReleases');
    }
  },
  {
    key: 'never',
    get value() {
      return translate('Never');
    }
  }
];

const rescanAfterRefreshOptions = [
  {
    key: 'always',
    get value() {
      return translate('Always');
    }
  },
  {
    key: 'afterManual',
    get value() {
      return translate('AfterManualRefresh');
    }
  },
  {
    key: 'never',
    get value() {
      return translate('Never');
    }
  }
];

const downloadPropersAndRepacksOptions = [
  {
    key: 'preferAndUpgrade',
    get value() {
      return translate('PreferAndUpgrade');
    }
  },
  {
    key: 'doNotUpgrade',
    get value() {
      return translate('DoNotUpgradeAutomatically');
    }
  },
  {
    key: 'doNotPrefer',
    get value() {
      return translate('DoNotPrefer');
    }
  }
];

const fileDateOptions = [
  {
    key: 'none',
    get value() {
      return translate('None');
    }
  },
  {
    key: 'localAirDate',
    get value() {
      return translate('LocalAirDate');
    }
  },
  {
    key: 'utcAirDate',
    get value() {
      return translate('UtcAirDate');
    }
  }
];

class MediaManagement extends Component {

  //
  // Listeners

  onDefaultProfileForAutoImportChange = ({ value }) => {
    const parsedProfileId = typeof value === 'string' ? parseInt(value) : value;

    if (!Number.isFinite(parsedProfileId)) {
      return;
    }

    this.props.onInputChange({ name: 'defaultProfileForAutoImport', value: parsedProfileId });
  };

  onSetRootFolderForAutoImportPress = (rootFolderPath) => {
    this.props.onInputChange({ name: 'defaultRootFolderForAutoImport', value: rootFolderPath });
  };

  //
  // Render

  render() {
    const {
      advancedSettings,
      isFetching,
      error,
      settings,
      hasSettings,
      isWindows,
      rootFolders,
      onInputChange,
      onSavePress,
      ...otherProps
    } = this.props;

    const rootFolderItems = (rootFolders && rootFolders.items) || [];
    const maxPathLength = rootFolderItems.reduce((maxValue, rootFolder) => {
      return Math.max(maxValue, (rootFolder.path || '').length);
    }, 0);

    const defaultRootFolderForAutoImportOptions = [
      {
        key: '',
        value: translate('DisableAutomaticImport')
      },
      ...rootFolderItems.map((rootFolder) => {
        const freeSpaceValue = typeof rootFolder.freeSpace === 'number' ? rootFolder.freeSpace : 0;
        const freeSpaceText = !rootFolder.accessible || rootFolder.freeSpace === undefined ? '-' : formatBytes(freeSpaceValue);
        const paddedPath = (rootFolder.path || '').padEnd(maxPathLength, '\u00A0');

        return {
          key: rootFolder.path,
          value: `${paddedPath}\u00A0\u00A0(${freeSpaceText})`
        };
      })
    ];

    const hasRootFolders = rootFolderItems.length > 0;

    return (
      <PageContent title={translate('MediaManagementSettings')}>
        <SettingsToolbarConnector
          advancedSettings={advancedSettings}
          {...otherProps}
          onSavePress={onSavePress}
        />

        <PageContentBody>
          <Naming />

          {
            isFetching ?
              <FieldSet legend={translate('NamingSettings')}>
                <LoadingIndicator />
              </FieldSet> : null
          }

          {
            !isFetching && error ?
              <FieldSet legend={translate('NamingSettings')}>
                <Alert kind={kinds.DANGER}>
                  {translate('MediaManagementSettingsLoadError')}
                </Alert>
              </FieldSet> : null
          }

          {
            hasSettings && !isFetching && !error ?
              <Form
                id="mediaManagementSettings"
                {...otherProps}
              >
                {
                  advancedSettings ?
                    <FieldSet legend={translate('Folders')}>
                      <FormGroup
                        advancedSettings={advancedSettings}
                        isAdvanced={true}
                        size={sizes.MEDIUM}
                      >
                        <FormLabel>{translate('CreateEmptySeriesFolders')}</FormLabel>

                        <FormInputGroup
                          type={inputTypes.CHECK}
                          name="createEmptySeriesFolders"
                          helpText={translate('CreateEmptySeriesFoldersHelpText')}
                          onChange={onInputChange}
                          {...settings.createEmptySeriesFolders}
                        />
                      </FormGroup>

                      <FormGroup
                        advancedSettings={advancedSettings}
                        isAdvanced={true}
                        size={sizes.MEDIUM}
                      >
                        <FormLabel>{translate('DeleteEmptyFolders')}</FormLabel>

                        <FormInputGroup
                          type={inputTypes.CHECK}
                          name="deleteEmptyFolders"
                          helpText={translate('DeleteEmptySeriesFoldersHelpText')}
                          onChange={onInputChange}
                          {...settings.deleteEmptyFolders}
                        />
                      </FormGroup>
                    </FieldSet> : null
                }

                {
                  advancedSettings ?
                    <FieldSet
                      legend={translate('Importing')}
                    >
                      <FormGroup
                        advancedSettings={advancedSettings}
                        isAdvanced={true}
                        size={sizes.SMALL}
                      >
                        <FormLabel>{translate('EpisodeTitleRequired')}</FormLabel>

                        <FormInputGroup
                          type={inputTypes.SELECT}
                          name="episodeTitleRequired"
                          helpText={translate('EpisodeTitleRequiredHelpText')}
                          values={episodeTitleRequiredOptions}
                          onChange={onInputChange}
                          {...settings.episodeTitleRequired}
                        />
                      </FormGroup>

                      <FormGroup
                        advancedSettings={advancedSettings}
                        isAdvanced={true}
                        size={sizes.MEDIUM}
                      >
                        <FormLabel>{translate('SkipFreeSpaceCheck')}</FormLabel>

                        <FormInputGroup
                          type={inputTypes.CHECK}
                          name="skipFreeSpaceCheckWhenImporting"
                          helpText={translate('SkipFreeSpaceCheckHelpText')}
                          onChange={onInputChange}
                          {...settings.skipFreeSpaceCheckWhenImporting}
                        />
                      </FormGroup>

                      <FormGroup
                        advancedSettings={advancedSettings}
                        isAdvanced={true}
                        size={sizes.MEDIUM}
                      >
                        <FormLabel>{translate('MinimumFreeSpace')}</FormLabel>

                        <FormInputGroup
                          type={inputTypes.NUMBER}
                          unit='MB'
                          name="minimumFreeSpaceWhenImporting"
                          helpText={translate('MinimumFreeSpaceHelpText')}
                          onChange={onInputChange}
                          {...settings.minimumFreeSpaceWhenImporting}
                        />
                      </FormGroup>

                      <FormGroup
                        advancedSettings={advancedSettings}
                        isAdvanced={true}
                        size={sizes.MEDIUM}
                      >
                        <FormLabel>{translate('UseHardlinksInsteadOfCopy')}</FormLabel>

                        <FormInputGroup
                          type={inputTypes.CHECK}
                          name="copyUsingHardlinks"
                          helpText={translate('CopyUsingHardlinksSeriesHelpText')}
                          helpTextWarning={translate('CopyUsingHardlinksHelpTextWarning')}
                          onChange={onInputChange}
                          {...settings.copyUsingHardlinks}
                        />
                      </FormGroup>

                      <FormGroup
                        advancedSettings={advancedSettings}
                        isAdvanced={true}
                        size={sizes.MEDIUM}
                      >
                        <FormLabel>{translate('ImportUsingScript')}</FormLabel>

                        <FormInputGroup
                          type={inputTypes.CHECK}
                          name="useScriptImport"
                          helpText={translate('ImportUsingScriptHelpText')}
                          onChange={onInputChange}
                          {...settings.useScriptImport}
                        />
                      </FormGroup>

                      {
                        settings.useScriptImport.value ?
                          <FormGroup
                            advancedSettings={advancedSettings}
                            isAdvanced={true}
                          >
                            <FormLabel>{translate('ImportScriptPath')}</FormLabel>

                            <FormInputGroup
                              type={inputTypes.PATH}
                              includeFiles={true}
                              name="scriptImportPath"
                              helpText={translate('ImportScriptPathHelpText')}
                              onChange={onInputChange}
                              {...settings.scriptImportPath}
                            />
                          </FormGroup> : null
                      }

                      <FormGroup size={sizes.MEDIUM}>
                        <FormLabel>{translate('ImportExtraFiles')}</FormLabel>

                        <FormInputGroup
                          type={inputTypes.CHECK}
                          name="importExtraFiles"
                          helpText={translate('ImportExtraFilesEpisodeHelpText')}
                          onChange={onInputChange}
                          {...settings.importExtraFiles}
                        />
                      </FormGroup>

                      {
                        settings.importExtraFiles.value ?
                          <FormGroup
                            advancedSettings={advancedSettings}
                            isAdvanced={true}
                          >
                            <FormLabel>{translate('ImportExtraFiles')}</FormLabel>

                            <FormInputGroup
                              type={inputTypes.TEXT}
                              name="extraFileExtensions"
                              helpTexts={[
                                translate('ExtraFileExtensionsHelpText'),
                                translate('ExtraFileExtensionsHelpTextsExamples')
                              ]}
                              onChange={onInputChange}
                              {...settings.extraFileExtensions}
                            />
                          </FormGroup> : null
                      }
                    </FieldSet> : null
                }

                <FieldSet
                  legend={translate('FileManagement')}
                >
                  <FormGroup size={sizes.MEDIUM}>
                    <FormLabel>{translate('UnmonitorDeletedEpisodes')}</FormLabel>

                    <FormInputGroup
                      type={inputTypes.CHECK}
                      name="autoUnmonitorPreviouslyDownloadedEpisodes"
                      helpText={translate('UnmonitorDeletedEpisodesHelpText')}
                      onChange={onInputChange}
                      {...settings.autoUnmonitorPreviouslyDownloadedEpisodes}
                    />
                  </FormGroup>

                  <FormGroup
                    advancedSettings={advancedSettings}
                    isAdvanced={true}
                    size={sizes.MEDIUM}
                  >
                    <FormLabel>{translate('DownloadPropersAndRepacks')}</FormLabel>

                    <FormInputGroup
                      type={inputTypes.SELECT}
                      name="downloadPropersAndRepacks"
                      helpTexts={[
                        translate('DownloadPropersAndRepacksHelpText'),
                        translate('DownloadPropersAndRepacksHelpTextCustomFormat')
                      ]}
                      helpTextWarning={
                        settings.downloadPropersAndRepacks.value === 'doNotPrefer' ?
                          translate('DownloadPropersAndRepacksHelpTextWarning') :
                          undefined
                      }
                      values={downloadPropersAndRepacksOptions}
                      onChange={onInputChange}
                      {...settings.downloadPropersAndRepacks}
                    />
                  </FormGroup>

                  <FormGroup
                    advancedSettings={advancedSettings}
                    isAdvanced={true}
                    size={sizes.MEDIUM}
                  >
                    <FormLabel>{translate('AnalyseVideoFiles')}</FormLabel>

                    <FormInputGroup
                      type={inputTypes.CHECK}
                      name="enableMediaInfo"
                      helpText={translate('AnalyseVideoFilesHelpText')}
                      onChange={onInputChange}
                      {...settings.enableMediaInfo}
                    />
                  </FormGroup>

                  <FormGroup
                    advancedSettings={advancedSettings}
                    isAdvanced={true}
                  >
                    <FormLabel>{translate('RescanSeriesFolderAfterRefresh')}</FormLabel>

                    <FormInputGroup
                      type={inputTypes.SELECT}
                      name="rescanAfterRefresh"
                      helpText={translate('RescanAfterRefreshSeriesHelpText')}
                      helpTextWarning={translate('RescanAfterRefreshHelpTextWarning')}
                      values={rescanAfterRefreshOptions}
                      onChange={onInputChange}
                      {...settings.rescanAfterRefresh}
                    />
                  </FormGroup>

                  <FormGroup
                    advancedSettings={advancedSettings}
                    isAdvanced={true}
                  >
                    <FormLabel>{translate('ChangeFileDate')}</FormLabel>

                    <FormInputGroup
                      type={inputTypes.SELECT}
                      name="fileDate"
                      helpText={translate('ChangeFileDateHelpText')}
                      values={fileDateOptions}
                      onChange={onInputChange}
                      {...settings.fileDate}
                    />
                  </FormGroup>

                  <FormGroup
                    advancedSettings={advancedSettings}
                    isAdvanced={true}
                  >
                    <FormLabel>{translate('RecyclingBin')}</FormLabel>

                    <FormInputGroup
                      type={inputTypes.PATH}
                      name="recycleBin"
                      helpText={translate('RecyclingBinHelpText')}
                      onChange={onInputChange}
                      {...settings.recycleBin}
                    />
                  </FormGroup>

                  <FormGroup
                    advancedSettings={advancedSettings}
                    isAdvanced={true}
                  >
                    <FormLabel>{translate('RecyclingBinCleanup')}</FormLabel>

                    <FormInputGroup
                      type={inputTypes.NUMBER}
                      name="recycleBinCleanupDays"
                      helpText={translate('RecyclingBinCleanupHelpText')}
                      helpTextWarning={translate('RecyclingBinCleanupHelpTextWarning')}
                      min={0}
                      onChange={onInputChange}
                      {...settings.recycleBinCleanupDays}
                    />
                  </FormGroup>
                </FieldSet>

                {
                  advancedSettings && !isWindows ?
                    <FieldSet
                      legend={translate('Permissions')}
                    >
                      <FormGroup
                        advancedSettings={advancedSettings}
                        isAdvanced={true}
                        size={sizes.MEDIUM}
                      >
                        <FormLabel>{translate('SetPermissions')}</FormLabel>

                        <FormInputGroup
                          type={inputTypes.CHECK}
                          name="setPermissionsLinux"
                          helpText={translate('SetPermissionsLinuxHelpText')}
                          helpTextWarning={translate('SetPermissionsLinuxHelpTextWarning')}
                          onChange={onInputChange}
                          {...settings.setPermissionsLinux}
                        />
                      </FormGroup>

                      <FormGroup
                        advancedSettings={advancedSettings}
                        isAdvanced={true}
                      >
                        <FormLabel>{translate('ChmodFolder')}</FormLabel>

                        <FormInputGroup
                          type={inputTypes.UMASK}
                          name="chmodFolder"
                          helpText={translate('ChmodFolderHelpText')}
                          helpTextWarning={translate('ChmodFolderHelpTextWarning')}
                          onChange={onInputChange}
                          {...settings.chmodFolder}
                        />
                      </FormGroup>

                      <FormGroup
                        advancedSettings={advancedSettings}
                        isAdvanced={true}
                      >
                        <FormLabel>{translate('ChownGroup')}</FormLabel>

                        <FormInputGroup
                          type={inputTypes.TEXT}
                          name="chownGroup"
                          helpText={translate('ChownGroupHelpText')}
                          helpTextWarning={translate('ChownGroupHelpTextWarning')}
                          values={fileDateOptions}
                          onChange={onInputChange}
                          {...settings.chownGroup}
                        />
                      </FormGroup>
                    </FieldSet> : null
                }
              </Form> : null
          }

          <FieldSet legend={translate('RootFolders')}>
            {
              hasSettings && settings.defaultRootFolderForAutoImport ?
                <FormGroup size={sizes.MEDIUM}>
                  <FormLabel>{translate('DefaultRootFolderForAutoImport')}</FormLabel>

                  {
                    hasRootFolders ?
                      <FormInputGroup
                        className={styles.defaultRootFolderForAutoImportSelect}
                        type={inputTypes.SELECT}
                        name="defaultRootFolderForAutoImport"
                        values={defaultRootFolderForAutoImportOptions}
                        onChange={onInputChange}
                        {...settings.defaultRootFolderForAutoImport}
                      /> :
                      <div style={{ paddingTop: 6 }}>
                        {translate('NoRootFolders')}
                      </div>
                  }
                </FormGroup> : null
            }

            {
              hasSettings && settings.defaultProfileForAutoImport ?
                <FormGroup size={sizes.MEDIUM}>
                  <FormLabel>{translate('QualityProfile')}</FormLabel>

                  <FormInputGroup
                    type={inputTypes.QUALITY_PROFILE_SELECT}
                    name="defaultProfileForAutoImport"
                    onChange={this.onDefaultProfileForAutoImportChange}
                    {...settings.defaultProfileForAutoImport}
                  />
                </FormGroup> : null
            }

            <RootFolders
              onSetRootFolderForAutoImportPress={this.onSetRootFolderForAutoImportPress}
            />
            <AddRootFolder />
          </FieldSet>
        </PageContentBody>
      </PageContent>
    );
  }

}

MediaManagement.propTypes = {
  advancedSettings: PropTypes.bool.isRequired,
  isFetching: PropTypes.bool.isRequired,
  error: PropTypes.object,
  settings: PropTypes.object.isRequired,
  hasSettings: PropTypes.bool.isRequired,
  isWindows: PropTypes.bool.isRequired,
  rootFolders: PropTypes.object.isRequired,
  onSavePress: PropTypes.func.isRequired,
  onInputChange: PropTypes.func.isRequired
};

export default MediaManagement;
