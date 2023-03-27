import React, { useCallback, useState } from 'react';
import { useSelector } from 'react-redux';
import { createSelector } from 'reselect';
import Form from 'Components/Form/Form';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { inputTypes, kinds, sizes } from 'Helpers/Props';
import Language from 'Language/Language';
import createLanguagesSelector from 'Store/Selectors/createLanguagesSelector';
import styles from './SelectLanguageModalContent.css';

interface SelectLanguageModalContentProps {
  languageIds: number[];
  modalTitle: string;
  onLanguagesSelect(languages: Language[]): void;
  onModalClose(): void;
}

function createFilteredLanguagesSelector() {
  return createSelector(createLanguagesSelector(), (languages) => {
    const { isFetching, isPopulated, error, items } = languages;

    const filterItems = ['Any', 'Original'];
    const filteredLanguages = items.filter(
      (lang) => !filterItems.includes(lang.name)
    );

    return {
      isFetching,
      isPopulated,
      error,
      items: filteredLanguages,
    };
  });
}

function SelectLanguageModalContent(props: SelectLanguageModalContentProps) {
  const { modalTitle, onLanguagesSelect, onModalClose } = props;

  const { isFetching, isPopulated, error, items } = useSelector(
    createFilteredLanguagesSelector()
  );

  const [languageIds, setLanguageIds] = useState(props.languageIds);

  const onLanguageChange = useCallback(
    ({ value, name }) => {
      const changedId = parseInt(name);

      let newLanguages = [...languageIds];

      if (value) {
        newLanguages.push(changedId);
      } else {
        newLanguages = languageIds.filter((i) => i !== changedId);
      }

      setLanguageIds(newLanguages);
    },
    [languageIds, setLanguageIds]
  );

  const onLanguagesSelectWrapper = useCallback(() => {
    const languages = items.filter((lang) => languageIds.includes(lang.id));

    onLanguagesSelect(languages);
  }, [items, languageIds, onLanguagesSelect]);

  return (
    <ModalContent onModalClose={onModalClose}>
      <ModalHeader>{modalTitle} - Select Language</ModalHeader>

      <ModalBody>
        {isFetching ? <LoadingIndicator /> : null}

        {!isFetching && error ? <div>Unable To Load Languages</div> : null}

        {isPopulated && !error ? (
          <Form>
            {items.map((language) => {
              return (
                <FormGroup
                  key={language.id}
                  size={sizes.EXTRA_SMALL}
                  className={styles.languageInput}
                >
                  <FormLabel>{language.name}</FormLabel>
                  <FormInputGroup
                    type={inputTypes.CHECK}
                    name={language.id.toString()}
                    value={languageIds.includes(language.id)}
                    onChange={onLanguageChange}
                  />
                </FormGroup>
              );
            })}
          </Form>
        ) : null}
      </ModalBody>

      <ModalFooter>
        <Button onPress={onModalClose}>Cancel</Button>

        <Button kind={kinds.SUCCESS} onPress={onLanguagesSelectWrapper}>
          Select Languages
        </Button>
      </ModalFooter>
    </ModalContent>
  );
}

export default SelectLanguageModalContent;
